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
#include <cstdlib>
#include <iomanip>
#include <map>
#include <memory>
#include <string>
#include <sstream>

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
    // Pseudo-elements, which this fixture answered `nullptr` to for every
    // name -- so ::before, ::after and ::marker were all invisible to a layout
    // test and a rule targeting one looked like an engine that ignored it.
    // Computed on demand and kept, since the box builder asks per element.
    std::map<std::pair<const Element*, std::string>, ComputedStyle*> pseudos;
    const ComputedStyle* pseudo_style_of(const Element& e, std::string_view name) override {
        const std::pair<const Element*, std::string> key{&e, std::string(name)};
        auto known = pseudos.find(key);
        if (known != pseudos.end()) return known->second;
        const ComputedStyle* host = style_of(e);
        if (!host) return nullptr;
        auto computed = std::make_unique<ComputedStyle>();
        if (!engine.compute_pseudo_element(e, name, state, *host, computed.get())) {
            pseudos[key] = nullptr;
            return nullptr;
        }
        ComputedStyle* raw = computed.get();
        owned.push_back(std::move(computed));
        pseudos[key] = raw;
        return raw;
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
    bool layout(std::string_view html, double vw = 1000, double vh = 600) {
        if (!build(html)) return false;
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
    // Text runs carry the element they came from too, and they precede the
    // inline boxes in a line's child list — so a plain find() for a span's id
    // returns its text, not its box. Anything asserting on an inline FRAGMENT
    // has to say so, or it passes with the feature removed.
    BoxId find_kind(std::string_view id, BoxKind kind, BoxId from = -2) const {
        const BoxId start = from == -2 ? root : from;
        if (start == kNoBox) return kNoBox;
        const Box& b = tree[start];
        if (b.kind == kind && b.element && b.element->get_attribute("id") == id) return start;
        for (BoxId c : tree.children(start)) {
            const BoxId hit = find_kind(id, kind, c);
            if (hit != kNoBox) return hit;
        }
        return kNoBox;
    }
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

void append_geometry(const BoxTree& tree, BoxId id, std::ostringstream& out) {
    const Box& box = tree[id];
    out << '(' << static_cast<int>(box.kind) << ':' << box.x << ',' << box.y << ','
        << box.width << ',' << box.height << ',' << box.baseline << ',' << box.text;
    for (BoxId child : tree.children(id)) append_geometry(tree, child, out);
    out << ')';
}

std::string geometry(const Fixture& f) {
    std::ostringstream out;
    out << std::setprecision(17);
    append_geometry(f.tree, f.root, out);
    return out.str();
}

} // namespace

void test_inline_layout_reuse() {
    const char* examples[] = {
        "<div id='text'>one two three four five six seven eight</div>",
        "<button id='text'>one two three four five six seven eight</button>",
        "<table><tr><td id='text'>one two three four five six seven eight</td>"
        "<td style='height:140px'>tall</td></tr></table>",
        "<div id='text'>one <b>two three</b> four "
        "<span style='display:inline-block;width:20%'>atom</span> five</div>",
        "<div style='display:flow-root'><div style='float:left;width:50%;height:60px'>float</div>"
        "<div id='text'>one two three four five six seven eight</div></div>",
        "<div id='text' style='white-space:pre-wrap'>a\nb\nc\nd\ne\nf\ng\nh\ni\nj</div>",
        "<div id='text' style='white-space:nowrap;overflow:hidden;text-overflow:ellipsis'>"
        "one two three four five six seven eight nine ten</div>"
    };
    const char* css = "body{margin:0;font-size:16px} #text{padding:5%;text-align:center}"
                      "button{display:block;width:100%;height:120px;box-sizing:border-box}"
                      "table{width:100%} td{vertical-align:middle}";
    for (const char* html : examples) {
        Fixture retained;
        CHECK(retained.css(css));
        CHECK(retained.build(html));
        BlockLayout layout(&retained.tree, retained.ctx, &retained.metrics);
        // Revisit nonconsecutive widths, including percentage padding and
        // post-layout button/cell alignment. Compare every reachable box
        // against a fresh tree, not only the outer element bounds.
        for (double width : {220.0, 140.0, 220.0, 360.0, 140.0, 220.0}) {
            layout.layout_root(retained.root, width, 600);
            Fixture fresh;
            CHECK(fresh.css(css));
            CHECK(fresh.layout(html, width, 600));
            CHECK_EQ(geometry(retained), geometry(fresh));
        }
    }

    // Prove an eligible A -> B -> A probe actually restores its first result.
    // The environment switch is used by independent old-path comparisons.
    Fixture f;
    CHECK(f.css("body{margin:0} #text{font-size:16px}"));
    CHECK(f.build(examples[0]));
    BlockLayout layout(&f.tree, f.ctx, &f.metrics);
    layout.layout_root(f.root, 100, 600);
    const auto first = f.lines("text");
    CHECK(first.size() > 1);
    layout.layout_root(f.root, 200, 600);
    layout.layout_root(f.root, 100, 600);
    CHECK(f.lines("text").size() == first.size());
    if (!std::getenv("WEVA_DISABLE_INLINE_REUSE")) CHECK(f.lines("text") == first);
}

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
    // Five, not three: entering the span emits a marker recording where the
    // inline box starts (so §9.4.2 can give it a fragment box even on a line
    // where it contributes no text of its own) and leaving it emits one for
    // where it ends, which carries the box's end edges.
    CHECK(items.size() == 5);
    CHECK(items[0].text == "one ");
    CHECK(items[1].is_inline_start());
    CHECK(items[2].text == "two");
    CHECK(items[3].is_inline_end());
    CHECK(items[4].text == " three");
    // The text inside the span is parented to it, the outer two are not; the
    // marker itself sits OUTSIDE the box it opens, which is what puts it at the
    // pen position where that box begins.
    CHECK(items[0].inline_parent == kNoBox);
    CHECK(items[1].inline_parent == kNoBox);
    CHECK(items[2].inline_parent != kNoBox);
    CHECK(items[1].inline_box_start == items[2].inline_parent);
    CHECK(near(items[2].font_size, 32));
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
        // Text runs only: a line's children now also carry the inline-box
        // fragments (§9.4.2), interleaved in document order.
        std::vector<BoxId> runs;
        for (BoxId c : f.tree.children(ls[0])) {
            if (f.tree[c].kind == BoxKind::Text) runs.push_back(c);
        }
        CHECK(runs.size() >= 2);
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

// CSS Sizing L3 §5: width: min-content | max-content | fit-content |
// fit-content(<length>) on blocks, floats and inline-blocks. The fixture's
// font is 8px per character at 16px: "ab cd ef" is 64 wide, its widest word 16.
void test_intrinsic_width_keywords() {
    Fixture f;
    CHECK(f.css("body { margin: 0; width: 1000px; font-size: 16px }"
                "#a { width: max-content } #b { width: min-content } #c { width: fit-content }"
                "#n { width: 50px } #n > div { width: fit-content }"
                "#d { width: fit-content(40px); padding: 5px } #e { width: max-content; padding: 5px }"
                "#bb { width: fit-content(40px); padding: 5px; box-sizing: border-box }"
                "#fl { float: left; width: max-content } #ib { display: inline-block; width: min-content }"
                "#h { width: max-content; max-width: 40px } #m { width: min-content; min-width: 30px }"));
    CHECK(f.layout("<body><div id=a>ab cd ef</div><div id=b>ab cd ef</div><div id=c>ab cd ef</div>"
                   "<div id=n><div id=nn>ab cd ef</div></div>"
                   "<div id=d>ab cd ef</div><div id=e>ab cd ef</div><div id=bb>ab cd ef</div>"
                   "<div id=w><div id=fl>ab cd ef</div></div><div id=x><span id=ib>ab cd ef</span></div>"
                   "<div id=h>ab cd ef</div><div id=m>ab cd ef</div></body>"));
    CHECK(near(f.tree[f.find("a")].width, 64));
    CHECK(near(f.tree[f.find("b")].width, 16));
    CHECK(near(f.tree[f.find("c")].width, 64));       // fits in 1000: max-content
    CHECK(near(f.tree[f.find("nn")].width, 50));      // clamped to the 50px available
    CHECK(near(f.tree[f.find("d")].width, 50));       // content 40 + 5px padding a side
    CHECK(near(f.tree[f.find("e")].width, 74));
    CHECK(near(f.tree[f.find("bb")].width, 40));      // border-box: the 40 is the whole box
    CHECK(near(f.tree[f.find("fl")].width, 64));
    CHECK(near(f.tree[f.find("h")].width, 40));
    CHECK(near(f.tree[f.find("m")].width, 30));
    // The inline-block: two lines of one word each, 16 wide.
    bool ib = false;
    for (BoxId c : f.tree.children(f.find("x"))) {
        for (BoxId r : f.tree.children(c)) {
            const Box& box = f.tree[r];
            if (box.element && box.element->get_attribute("id") == "ib" && near(box.width, 16)) ib = true;
        }
    }
    CHECK(ib);
}

// CSS Text L3 §7.3-7.4: text-align: justify, text-align-last, text-justify.
// The fixture's font is 8px per character at 16px, so a two-letter word is
// 16 wide and a space 8.
void test_text_align_justify() {
    const auto run = [](const Fixture& f, BoxId line, std::string_view text, int nth = 0) {
        int seen = 0;
        for (BoxId c : f.tree.children(line)) {
            if (f.tree[c].kind == BoxKind::Text && f.tree[c].text == text && seen++ == nth) return c;
        }
        return kNoBox;
    };
    {
        // The three gaps of a wrapped line share the 12px of slack; the last
        // line is `start`, and no whole-line delta is recorded.
        Fixture f;
        CHECK(f.css("#j { display: block; width: 100px; font-size: 16px; text-align: justify }"));
        CHECK(f.layout("<body><div id=j>ab cd ef gh ij</div></body>"));
        const auto lines = f.lines("j");
        CHECK(lines.size() == 2);
        const BoxId ab = run(f, lines[0], "ab"), sp = run(f, lines[0], " "), cd = run(f, lines[0], "cd"),
                    gh = run(f, lines[0], "gh"), ij = run(f, lines[1], "ij");
        CHECK(ab != kNoBox && sp != kNoBox && cd != kNoBox && gh != kNoBox && ij != kNoBox);
        CHECK(near(f.tree[ab].x, 0));
        CHECK(near(f.tree[sp].width, 12));
        CHECK(near(f.tree[sp].justify_extra_width, 4));
        CHECK(near(f.tree[cd].x, 28));
        CHECK(near(f.tree[gh].x, 84));
        CHECK(near(f.tree[gh].x + f.tree[gh].width, 100));
        CHECK(near(f.tree[ij].x, 0));
        CHECK(near(f.tree[lines[0]].applied_text_align_delta, 0));
    }
    {
        // text-align-last: justify spreads the final line too; center centres
        // it the ordinary way.
        Fixture f;
        CHECK(f.css("#j, #c { display: block; width: 100px; font-size: 16px; text-align: justify }"
                    "#j { text-align-last: justify } #c { text-align-last: center }"));
        CHECK(f.layout("<body><div id=j>ab cd ef gh ij kl</div><div id=c>ab cd ef gh ij</div></body>"));
        const auto lines = f.lines("j");
        CHECK(lines.size() == 2);
        CHECK(near(f.tree[run(f, lines[1], "kl")].x, 84));
        CHECK(near(f.tree[run(f, lines[1], " ")].width, 68));
        const auto cl = f.lines("c");
        CHECK(cl.size() == 2);
        CHECK(near(f.tree[run(f, cl[1], "ij")].x, 42));
        CHECK(near(f.tree[cl[1]].applied_text_align_delta, 42));
    }
    {
        // A line a forced break ends is a last line: not spread.
        Fixture f;
        CHECK(f.css("#j { display: block; width: 100px; font-size: 16px; text-align: justify }"));
        CHECK(f.layout("<body><div id=j>ab cd<br>ef</div></body>"));
        const auto lines = f.lines("j");
        CHECK(lines.size() == 2);
        CHECK(near(f.tree[run(f, lines[0], "cd")].x, 24));
    }
    {
        // text-justify: inter-character spreads the ten character boundaries
        // of "ab cd ef gh" (1.2 each): runs widen by their internal gaps and
        // carry the increment for paint; `none` leaves the line ragged.
        Fixture f;
        CHECK(f.css("#j, #n { display: block; width: 100px; font-size: 16px; text-align: justify }"
                    "#j { text-justify: inter-character } #n { text-justify: none }"));
        CHECK(f.layout("<body><div id=j>ab cd ef gh ij</div><div id=n>ab cd ef gh ij</div></body>"));
        const auto lines = f.lines("j");
        CHECK(lines.size() == 2);
        const BoxId ab = run(f, lines[0], "ab"), cd = run(f, lines[0], "cd"), gh = run(f, lines[0], "gh");
        CHECK(near(f.tree[ab].width, 17.2));
        CHECK(near(f.tree[ab].justify_letter_spacing, 1.2));
        CHECK(near(f.tree[ab].justify_extra_width, 1.2));
        CHECK(near(f.tree[cd].x, 27.6));
        CHECK(near(f.tree[gh].x, 82.8));
        CHECK(near(f.tree[gh].x + f.tree[gh].width, 100));
        const auto nl = f.lines("n");
        CHECK(nl.size() == 2);
        CHECK(near(f.tree[run(f, nl[0], "gh")].x, 72));
    }
    {
        // An inline box's edges ride along with the spread, and an atom on the
        // line moves with the words around it.
        Fixture f;
        CHECK(f.css("#j { display: block; width: 100px; font-size: 16px; text-align: justify }"
                    "i { display: inline-block; width: 16px; height: 10px }"));
        CHECK(f.layout("<body><div id=j>ab <b>cd</b> <i></i> gh ij</div></body>"));
        const auto lines = f.lines("j");
        CHECK(lines.size() == 2);
        // "ab cd [atom] gh" is 16+8+16+8+16+8+16 = 88 -> 12 of slack over 3 gaps.
        CHECK(near(f.tree[run(f, lines[0], "cd")].x, 28));
        CHECK(near(f.tree[run(f, lines[0], "gh")].x, 84));
        const BoxId atom = f.find_kind("", BoxKind::Block, lines[0]);
        (void)atom;
        bool atom_moved = false;
        for (BoxId c : f.tree.children(lines[0])) {
            if (f.tree[c].kind == BoxKind::Block && near(f.tree[c].x, 56)) atom_moved = true;
        }
        CHECK(atom_moved);
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
        // CSS 2.1 §10.3.5 computes
        // min(preferred, max(preferred-minimum, available)), which for a 20px
        // container and a 40px longest word gives 40 — the float overflows
        // rather than squeezing below its min-content width. The old reference
        // clamp to available space hid this overflow and is deliberately fixed.
        Fixture f;
        CHECK(f.css("#w { display: block; width: 20px; font-size: 16px }"
                    "#fl { float: left }"));
        CHECK(f.layout("<body><div id=w><div id=fl>hello world</div></div></body>"));
        CHECK(near(f.box("fl").width, 40));
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
void test_form_control_baselines() {
    // An independent ordinary inline-block centers text with a line-height
    // equal to its content height. Editable inputs expose the same baseline.
    for (const auto type : {"text", "search", "tel", "url", "email", "password", "number",
                            "date", "month", "week", "time", "datetime-local", "TEXT", "unknown"}) {
        for (int fs : {10, 16, 28}) for (int height : {12, 34, 60}) {
            for (const auto overflow : {"visible", "hidden", "auto", "clip"}) {
                for (int margin : {-3, 0, 7}) {
                    Fixture f;
                    const int top = 3, bottom = 5, border = 1;
                    const int content = height - top - bottom - border * 2;
                    const std::string common =
                        "{display:inline-block;box-sizing:border-box;width:120px;height:" + std::to_string(height) +
                        "px;border:1px solid;padding:3px 4px 5px;margin:" + std::to_string(margin) +
                        "px 0 2px;font-size:" + std::to_string(fs) + "px}";
                    CHECK(f.css("#field,#model" + common + "#field{overflow:" + overflow +
                                ";line-height:0}#model{line-height:" + std::to_string(content) + "px}"));
                    CHECK(f.layout("<div><input id=field type='" + std::string(type) +
                                   "'><span id=model>X</span></div>"));
                    CHECK(near(f.box("field").y, f.box("model").y));
                    CHECK(near(f.box("field").height, height));
                }
            }
        }
    }
    for (const auto type : {"checkbox", "radio", "range", "image", "CHECKBOX"}) {
        for (const auto overflow : {"visible", "hidden", "auto"}) {
            Fixture f;
            CHECK(f.css("#field,#model{display:inline-block;box-sizing:border-box;width:40px;height:34px;"
                        "border:2px solid;padding:3px 4px;margin:7px 0 5px}#model{overflow:hidden}"
                        "#field{overflow:" + std::string(overflow) + "}#model{margin-bottom:" +
                        (std::string(type) == "image" ? "5px" : "0") + "}"));
            CHECK(f.layout("<div><input id=field type='" + std::string(type) + "'><span id=model></span></div>"));
            CHECK(near(f.box("field").y, f.box("model").y));
        }
    }
}

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
        // Browser geometry exposes the font box, even for a shorter line-height.
        CHECK(near(f.tree[br].height, f.metrics.ascent(16) + f.metrics.descent(16)));
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

// CSS 2.1 §9.4.2. The port flattened inline elements into text runs and let
// BoxTree::clear_children orphan the inline boxes, so a `<span>` produced no box
// at all: paint could not draw its background or border, and hit testing had
// nothing to surface a click on. The oracle found it as two missing elements.
void test_inline_fragments() {
    {
        // One line: the span's box spans exactly its own text.
        Fixture f;
        CHECK(f.css("#w { display: block; width: 400px; font-size: 16px }"
                    "#s { background-color: #f00 }"));
        CHECK(f.layout("<body><div id=w>one <span id=s>two</span> three</div></body>"));
        const BoxId s = f.find_kind("s", BoxKind::Inline);
        CHECK(s != kNoBox);
        const Box& sb = f.tree[s];
        // "one " is 4 chars at 8px.
        CHECK(near(sb.x, 32));
        CHECK(near(sb.width, 24));
        // As tall as the font, not as the line.
        CHECK(near(sb.height, 16 * 0.8 + 16 * 0.4));
        // It hangs off the line box, beside the runs rather than around them.
        CHECK(f.tree[sb.parent].kind == BoxKind::Line);
    }
    {
        // An inline box with no content of its own still gets a box, at the pen
        // where it begins. This is the shape block-in-inline splitting leaves
        // behind when the block moves out of the inline.
        Fixture f;
        CHECK(f.css("#w { display: block; width: 400px; font-size: 16px }"));
        CHECK(f.layout("<body><div id=w>one <span id=s></span>two</div></body>"));
        const BoxId s = f.find_kind("s", BoxKind::Inline);
        CHECK(s != kNoBox);
        CHECK(near(f.tree[s].width, 0));
        CHECK(near(f.tree[s].x, 32));
    }
    {
        // Nested inlines each get a box, and the outer one spans the inner.
        Fixture f;
        CHECK(f.css("#w { display: block; width: 400px; font-size: 16px }"));
        CHECK(f.layout("<body><div id=w>a <span id=o>b <span id=i>c</span></span></div></body>"));
        const BoxId o = f.find_kind("o", BoxKind::Inline);
        const BoxId i = f.find_kind("i", BoxKind::Inline);
        CHECK(o != kNoBox && i != kNoBox);
        CHECK(f.tree[o].x <= f.tree[i].x);
        CHECK(f.tree[o].x + f.tree[o].width >= f.tree[i].x + f.tree[i].width);
    }
}

// Three rules about inline boxes that the harvested corpus found, each pulling
// against the others. They are one test because getting any one right in
// isolation is easy and getting all three right together is the actual problem.
void test_inline_fragment_edges() {
    {
        // A line's children are read first-box-per-element by the dump, by
        // paint and by hit testing, so a fragment has to precede the runs and
        // atoms it sits among — the reference inserts every fragment FIRST.
        // Appending them afterwards put a <label> after the <input> that
        // follows it in source.
        Fixture f;
        CHECK(f.css("#w { display: block; width: 400px; font-size: 16px }"
                    "#i { display: inline-block; width: 20px; height: 10px }"));
        CHECK(f.layout("<body><div id=w><span id=s>Name</span>"
                       "<span id=i></span></div></body>"));
        const BoxId line = f.lines("w")[0];
        int span_index = -1, atom_index = -1, k = 0;
        for (BoxId c : f.tree.children(line)) {
            const Box& b = f.tree[c];
            if (b.element && b.element->get_attribute("id") == "s" &&
                b.kind == BoxKind::Inline && span_index < 0) {
                span_index = k;
            }
            if (b.element && b.element->get_attribute("id") == "i") atom_index = k;
            ++k;
        }
        CHECK(span_index >= 0 && atom_index >= 0);
        CHECK(span_index < atom_index);
    }
    {
        // CSS 2.1 §9.4.2: a line box holding no text, no preserved whitespace
        // and no inline with a margin, padding or border is ZERO-HEIGHT, so an
        // empty inline gives its container no height at all.
        //
        // This asserted 16 — "the strut's height rather than none" — until
        // Chrome was captured on the markup: it gives this div, one holding
        // `<span> </span>`, and one holding a nested empty pair, a height of 0
        // apiece. Three harvested cases were exactly this shape.
        Fixture f;
        CHECK(f.css("#w { display: block; width: 400px; font-size: 16px;"
                    "     line-height: 1 }"));
        CHECK(f.layout("<body><div id=w><span id=s></span></div></body>"));
        CHECK(near(f.box("w").height, 0));
    }
    {
        // ...but it gets no fragment box, because a fragment is earned by
        // covering content and there is none on that line.
        Fixture f;
        CHECK(f.css("#w { display: block; width: 400px; font-size: 16px }"));
        CHECK(f.layout("<body><div id=w><span id=s></span></div></body>"));
        CHECK(f.find_kind("s", BoxKind::Inline) == kNoBox);
    }
    {
        // An empty inline box on a line that DOES have content must appear —
        // this is the shape block-in-inline splitting leaves behind, and it is
        // the case that stops the rule above from being "drop empty spans".
        Fixture f;
        CHECK(f.css("#w { display: block; width: 400px; font-size: 16px }"));
        CHECK(f.layout("<body><div id=w>Click <span id=s></span></div></body>"));
        const BoxId s = f.find_kind("s", BoxKind::Inline);
        CHECK(s != kNoBox);
        CHECK(near(f.tree[s].width, 0));
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
        // CSS 2.1 §10.8: the second line still carries the block's strut even
        // though no text lands on it, so the 10px atom sits on the baseline
        // with the strut's ascent above it — not flush with the line's top.
        // Verified against Chrome on this exact markup: it puts the span 3px
        // into a second line that is a full line-height tall. Before the strut
        // existed the line was exactly the atom's 10px and this read 0.
        CHECK(near(f.box("a").y, f.metrics.ascent(16) - 10));
    }
}

void test_anonymous_block_inherits_text_align() {
    // CSS 2.1 §9.2.1.1: an anonymous block inherits from its parent. It has no
    // style of its own, so text-align has to be read off the parent — the way
    // line-height already is. Read off the null style it resolved to `start`,
    // and an atom that shared a right-aligned parent with a block sibling (so
    // it sat in an anonymous block) was flushed left.
    {
        Fixture f;
        CHECK(f.css("#w { width: 200px; text-align: right }"
                    "#blk { height: 10px }"
                    "#pill { display: inline-block; width: 50px; height: 10px }"));
        CHECK(f.layout("<body><div id=w><div id=blk></div><span id=pill></span></div></body>"));
        CHECK(near(f.box("pill").x, 150));
    }
    {
        // center, and text rather than an atom, through the same path.
        Fixture f;
        CHECK(f.css("#w { width: 200px; text-align: center }"
                    "#blk { height: 10px }"));
        CHECK(f.layout("<body><div id=w><div id=blk></div>ab</div></body>"));
        // Two 'a'-width mono glyphs, centred: the line's delta is half the slack.
        const Box& w = f.box("w");
        BoxId anon = kNoBox;
        for (BoxId c : f.tree.children(f.find("w"))) {
            if (f.tree[c].kind == BoxKind::AnonymousBlock) anon = c;
        }
        CHECK(anon != kNoBox);
        BoxId line = kNoBox;
        for (BoxId c : f.tree.children(anon)) {
            if (f.tree[c].kind == BoxKind::Line) line = c;
        }
        CHECK(line != kNoBox);
        double text_w = 0;
        for (BoxId c : f.tree.children(line)) text_w += f.tree[c].width;
        CHECK(text_w > 0 && text_w < w.width);
        CHECK(near(f.tree[line].applied_text_align_delta, (w.width - text_w) * 0.5));
    }
}

void test_letter_spacing_widens_runs() {
    // Chrome includes spacing after the final typographic character too.
    // Five characters gain five spacings; a preserved space gains one.
    Fixture plain, spaced;
    CHECK(plain.css("#w { width: 1000px; white-space: nowrap }"));
    CHECK(spaced.css("#w { width: 1000px; white-space: nowrap; letter-spacing: 2px }"));
    CHECK(plain.layout("<body><div id=w>Hello world</div></body>"));
    CHECK(spaced.layout("<body><div id=w>Hello world</div></body>"));
    double plain_w = 0, spaced_w = 0;
    for (BoxId c : plain.tree.children(plain.lines("w")[0])) plain_w += plain.tree[c].width;
    for (BoxId c : spaced.tree.children(spaced.lines("w")[0])) spaced_w += spaced.tree[c].width;
    // The whole run: "Hello world" is 11 characters, 10 gaps — spaces count,
    // as in the reference's single-line measure.
    CHECK(near(spaced_w - plain_w, 22));
    // em resolves against the run's own font size.
    Fixture em;
    CHECK(em.css("#w { width: 1000px; white-space: nowrap; font-size: 20px;"
                 "     letter-spacing: 0.5em }"));
    CHECK(em.layout("<body><div id=w>ab</div></body>"));
    Fixture em0;
    CHECK(em0.css("#w { width: 1000px; white-space: nowrap; font-size: 20px }"));
    CHECK(em0.layout("<body><div id=w>ab</div></body>"));
    double a = 0, b = 0;
    for (BoxId c : em.tree.children(em.lines("w")[0])) a += em.tree[c].width;
    for (BoxId c : em0.tree.children(em0.lines("w")[0])) b += em0.tree[c].width;
    CHECK(near(a - b, 20));
}

void test_inline_fragment_height_and_order() {
    {
        // A fragment is as tall as ITS font, not the root's: the box builder
        // never stamps a font size on an inline box.
        Fixture f;
        CHECK(f.css("#p { font-size: 35px; line-height: 1.28; width: 800px }"));
        CHECK(f.layout("<body><p id=p>Welcome, <span id=hl>Matt</span>!</p></body>"));
        const Box& hl = f.tree[f.find_kind("hl", BoxKind::Inline)];
        CHECK(near(hl.height, f.metrics.ascent(35) + f.metrics.descent(35)));
    }
    {
        // Fragments are inserted FIRST on the line, later-opened before
        // earlier-opened, all before the runs — the reference's
        // InsertChildFirst order, which is what the dump walks.
        Fixture f;
        CHECK(f.css("#p { width: 800px }"));
        CHECK(f.layout("<body><p id=p>Edit <code id=c>menu.css</code> then <kbd id=k>F12</kbd>."
                       "</p></body>"));
        const std::vector<BoxId> ls = f.lines("p");
        CHECK(ls.size() == 1);
        std::vector<BoxId> kids;
        for (BoxId c : f.tree.children(ls[0])) kids.push_back(c);
        CHECK(kids.size() >= 3);
        CHECK(f.tree[kids[0]].kind == BoxKind::Inline);
        CHECK(f.tree[kids[0]].element->get_attribute("id") == "k");
        CHECK(f.tree[kids[1]].kind == BoxKind::Inline);
        CHECK(f.tree[kids[1]].element->get_attribute("id") == "c");
        CHECK(f.tree[kids[2]].kind == BoxKind::Text);
        // Geometry is unaffected by the order: code still sits before kbd.
        CHECK(f.tree[kids[1]].x < f.tree[kids[0]].x);
    }
}

void test_font_family_registry() {
    // A registered family wins for any stack that names it; an unknown head
    // is skipped, and nothing registered falls back to the default face.
    const MonoFontMetrics mono = MonoFontMetrics::chrome_monospace();
    Fixture f;
    f.ctx.register_font("monospace", &mono);
    CHECK(f.css("#w { width: 1000px; white-space: nowrap; font-size: 20px }"
                "#c { font-family: \"Courier New\", monospace }"
                "#s { font-family: Arial, sans-serif }"));
    CHECK(f.layout("<body><div id=w><span id=c>abcd</span><span id=s>abcd</span></div></body>"));
    const Box& c = f.tree[f.find_kind("c", BoxKind::Inline)];
    const Box& s = f.tree[f.find_kind("s", BoxKind::Inline)];
    // The fixture's default face is 0.5em per glyph; monospace is 0.6em.
    CHECK(near(c.width, 4 * 20 * 0.6));
    CHECK(near(s.width, 4 * 20 * 0.5));
    LayoutContext ctx;
    ctx.register_font("Monospace", &mono);
    CHECK(ctx.font_for("'monospace'") == &mono);
    CHECK(ctx.font_for("Sniglet, \"Baloo 2\", monospace") == &mono);
    CHECK(ctx.font_for("Sniglet, sans-serif") == nullptr);
    CHECK(ctx.font_for("") == nullptr);
}

void test_letter_spacing_counts_graphemes() {
    // An astral emoji is one typographic character and gains one spacing.
    Fixture a, b;
    CHECK(a.css("#w { width: 1000px; white-space: nowrap; font-size: 32px }"));
    CHECK(b.css("#w { width: 1000px; white-space: nowrap; font-size: 32px;"
                "     letter-spacing: 0.01em }"));
    CHECK(a.layout("<body><div id=w>\xF0\x9F\x98\x80</div></body>"));
    CHECK(b.layout("<body><div id=w>\xF0\x9F\x98\x80</div></body>"));
    double wa = 0, wb = 0;
    for (BoxId c : a.tree.children(a.lines("w")[0])) wa += a.tree[c].width;
    for (BoxId c : b.tree.children(b.lines("w")[0])) wb += b.tree[c].width;
    CHECK(near(wb - wa, 0.32));
}

void test_inline_box_opening_at_line_end_has_no_fragment_there() {
    // A `<code>` that opens at the very end of a line and whose text wraps
    // gets its first box on the NEXT line, where its content is — the
    // reference emits no zero-width fragment on the first line. An inline
    // with no content anywhere still gets one where it opens.
    Fixture f;
    // 0.5em per glyph at 16px: 8px a character. "aaaaaaaaaa " fills 88 of 100;
    // "bbbbbb" (48) wraps.
    CHECK(f.css("#p { width: 100px; font-size: 16px }"));
    CHECK(f.layout("<body><p id=p>aaaaaaaaaa <code id=c>bbbbbb</code> <span id=e></span></p>"
                   "</body>"));
    const std::vector<BoxId> ls = f.lines("p");
    CHECK(ls.size() == 2);
    bool code_on_first = false, code_on_second = false, empty_span_found = false;
    for (BoxId c : f.tree.children(ls[0])) {
        const Box& b = f.tree[c];
        if (b.kind == BoxKind::Inline && b.element->get_attribute("id") == "c") code_on_first = true;
    }
    for (BoxId c : f.tree.children(ls[1])) {
        const Box& b = f.tree[c];
        if (b.kind == BoxKind::Inline && b.element->get_attribute("id") == "c") code_on_second = true;
        if (b.kind == BoxKind::Inline && b.element->get_attribute("id") == "e") empty_span_found = true;
    }
    CHECK(!code_on_first);
    CHECK(code_on_second);
    CHECK(empty_span_found);
}

void test_inline_em_font_size_resolves_against_the_parent() {
    // `<small>` is 0.83em in the UA sheet; inside a 14px label that is 11.62,
    // not 0.83 of the root. The run's style is its element's, so the basis
    // is that element's parent.
    Fixture f;
    CHECK(f.css("#l { font-size: 14px; width: 500px }"));
    CHECK(f.layout("<body><div id=l>Hits <small id=s>x</small></div></body>"));
    const Box& s = f.tree[f.find_kind("s", BoxKind::Inline)];
    const double fs = 14 * 0.83;
    CHECK(near(s.height, f.metrics.ascent(fs) + f.metrics.descent(fs)));
}

void test_max_content_joins_wrapped_lines() {
    // A paragraph laid out narrow and asked for its max-content width answers
    // with the whole text, not its widest wrapped line: a centred paragraph
    // in a column flex fits to the column, not to a line.
    Fixture f;
    CHECK(f.css("#info { display: flex; flex-direction: column; align-items: center; width: 194px }"
                "#d { margin: 0 }"));
    CHECK(f.layout("<body><div id=info><p id=d>Build the roads that get the millions of "
                   "commuters to work on time.</p></div></body>"));
    CHECK(near(f.box("d").width, 194));
    CHECK(near(f.box("d").x, 0));
    // A short paragraph still fits its text, and a trailing space in the
    // source does not widen it.
    Fixture g;
    CHECK(g.css("#info { display: flex; flex-direction: column; align-items: center; width: 194px }"
                "#d { margin: 0 }"));
    CHECK(g.layout("<body><div id=info><p id=d>ab cd </p></div></body>"));
    CHECK(near(g.box("d").width, 5 * 8));
    // Anonymous text items in a flex row: " Back " is as wide as "Back".
    Fixture h;
    CHECK(h.css("#r { display: flex; align-items: center; gap: 10px; width: 600px }"
                "#a { width: 26px; height: 26px }"));
    CHECK(h.layout("<body><div id=r><span id=a></span> Back <span id=b class=x></span></div></body>"));
    CHECK(near(h.box("b").x, 26 + 10 + 4 * 8 + 10));
}

void test_parent_intrinsic_measurements() {
    struct Example { const char* css; const char* html; double minimum; double maximum; };
    const Example examples[] = {
        {"width:40px", "aa bbbb cc", 32, 80},
        {"width:40px;white-space:nowrap", "aa bbbb cc", 80, 80},
        {"width:40px;white-space:pre", "aa bbbb", 56, 56},
        {"width:40px;white-space:pre", "aa bbbb\ncc", 56, 56},
        {"width:40px;white-space:pre", "\naa bbbb\n\ncc\n", 56, 56},
        {"width:40px;white-space:pre", "aa <span>bbbb\ncc</span>", 56, 56},
        {"width:40px;white-space:pre-wrap", "aa bbbb\ncc", 32, 56},
        {"width:40px;white-space:pre-line", "aa bbbb\ncc", 32, 56},
        {"width:40px;white-space:pre-line", "aa bbbb  \n  cc", 32, 56},
        {"width:40px", "aa bbbb\ncc", 32, 80},
        {"width:40px;white-space:nowrap", "aa bbbb\ncc", 80, 80},
        {"width:40px", "aa bbbb<br>cc", 32, 56},
        {"width:40px", "<b style='padding:0 4px'>aa bbbb</b>", 32, 64},
        {"display:flex;width:200px;gap:10px;flex-wrap:wrap",
         "<div>aa bbbb</div><div>cc ddd</div>", 32, 114},
        {"display:flex;width:200px;gap:10px;flex-wrap:nowrap",
         "<div>aa bbbb</div><div>cc ddd</div>", 66, 114},
        {"display:flex;width:200px;gap:10px;flex-direction:column",
         "<div>aa bbbb</div><div>cc ddd</div>", 32, 56},
        {"display:grid;width:200px;grid-template-columns:30px 50px;gap:10px",
         "<div>aa</div><div>cc</div>", 90, 90},
        {"width:200px", "<div style='width:40px;padding:0 3px;margin:0 auto'>a</div>", 46, 46},
        {"width:200px", "<div style='width:50%;padding:0 3px'>aa bbbb</div>", 38, 62},
        {"width:200px", "<div style='min-width:70px;max-width:90px;padding:0 3px'>aa bbbb</div>", 76, 76},
        {"width:200px", "<div style='max-width:40px;box-sizing:border-box;padding:0 3px'>aa bbbb</div>", 38, 40},
        {"width:200px", "<div>aa</div><div style='position:absolute;width:500px'>x</div>"
                        "<div style='position:fixed;width:600px'>x</div>"
                        "<div style='float:left;width:700px'>x</div>", 16, 16},
    };
    for (const auto& example : examples) {
        Fixture f;
        CHECK(f.css(std::string("#s { font-size:16px;") + example.css + "}"));
        CHECK(f.layout(std::string("<body><div id=s>") + example.html + "</div></body>"));
        const BoxId id = f.find("s");
        const auto measured = measure_parent_layout_input(f.tree, id, 200, f.ctx);
        if (!near(measured.min_content, example.minimum) || !near(measured.max_content, example.maximum)) {
            std::printf("intrinsic sizes for {%s} %s: got %.17g / %.17g; expected %.17g / %.17g\n",
                        example.css, example.html, measured.min_content, measured.max_content,
                        example.minimum, example.maximum);
        }
        CHECK(near(measured.min_content, example.minimum));
        CHECK(near(measured.max_content, example.maximum));
        CHECK(near(min_content_width(f.tree, id, &f.ctx), example.minimum));
        CHECK(near(max_content_width(f.tree, id, &f.ctx), example.maximum));
        CHECK(measured.available_width == 200);
        CHECK(measured.width == f.tree[id].width && measured.height == f.tree[id].height);
    }
    // Intrinsic contributions use current child geometry, even if the style
    // version is unchanged. Parent input capture must not retain a prior probe.
    Fixture f;
    CHECK(f.css("#s { width:200px } #c { width:40px; margin:0 auto }"));
    CHECK(f.layout("<body><div id=s><div id=c>a</div></div></body>"));
    const BoxId parent = f.find("s"), child = f.find("c");
    const auto before = measure_parent_layout_input(f.tree, parent, 200, f.ctx);
    CHECK(before.min_content == 40 && before.max_content == 40);
    f.tree[child].width = 65;
    const auto after = measure_parent_layout_input(f.tree, parent, 200, f.ctx);
    CHECK(after.min_content == 65 && after.max_content == 65);
    CHECK(after != before);

    // The measured width must reach actual shrink-to-fit, flex and grid
    // sizing, not just the standalone intrinsic-width entry points.
    for (const char* parent : {"display:block", "display:flex",
                               "display:grid;grid-template-columns:max-content"}) {
        Fixture sized;
        CHECK(sized.css(std::string("#p { width:200px;") + parent +
                        "} #s { display:inline-block; font-size:16px; white-space:pre }"));
        CHECK(sized.layout("<body><div id=p><div id=s>aa bbbb\ncc</div></div></body>"));
        CHECK(near(sized.box("s").width, 56));
    }
}

void test_inline_box_edges_take_space_on_the_line() {
    // CSS 2.1 §10.6.1: an inline box's horizontal padding, border and margin
    // are on the line — they advance the pen and belong to its fragment.
    {
        Fixture f;
        CHECK(f.css("#p { width: 600px; white-space: nowrap }"
                    "#c { padding: 2px 6px; border: 1px solid black; margin: 0 3px }"));
        CHECK(f.layout("<body><p id=p>ab <code id=c>cd</code> ef</p></body>"));
        const Box& c = f.tree[f.find_kind("c", BoxKind::Inline)];
        // 8px glyphs: "ab " = 24, then margin 3 -> the border edge at 27;
        // border 1 + padding 6 + "cd" 16 + padding 6 + border 1 = 30 wide.
        CHECK(near(c.x, 27));
        CHECK(near(c.width, 30));
        // " ef" starts after the right margin: 27 + 30 + 3 = 60.
        const std::vector<BoxId> ls = f.lines("p");
        double ef_x = -1;
        for (BoxId r : f.tree.children(ls[0])) {
            if (f.tree[r].kind == BoxKind::Text && f.tree[r].text == " ") {
                if (f.tree[r].x > 50) ef_x = f.tree[r].x;
            }
        }
        CHECK(near(ef_x, 60));
    }
    {
        // The edges count toward the max-content width, so a shrink-to-fit
        // container is wide enough for the padded badge.
        Fixture f;
        CHECK(f.css("#w { display: inline-block; white-space: nowrap }"
                    "#c { padding: 0 6px }"));
        CHECK(f.layout("<body><div id=p><span id=w>a<code id=c>b</code></span></div></body>"));
        CHECK(near(f.box("w").width, 8 + 6 + 8 + 6));
    }
    {
        // A box that wraps carries its start edge on the first line and its
        // end edge on the last only (box-decoration-break: slice).
        Fixture f;
        CHECK(f.css("#p { width: 60px }"
                    "#c { padding: 0 10px }"));
        CHECK(f.layout("<body><p id=p><code id=c>aaaa bbbb</code></p></body>"));
        const std::vector<BoxId> ls = f.lines("p");
        CHECK(ls.size() == 2);
        // Line 1: padding 10 + "aaaa" 32 = 42 (the space trimmed); line 2:
        // "bbbb" 32 + padding 10 = 42.
        const Box& first = f.tree[f.find_kind("c", BoxKind::Inline)];
        CHECK(near(first.x, 0) && near(first.width, 42));
    }
}

void test_leading_space_after_an_inline_start_is_dropped() {
    // Whitespace at the start of a line is collapsed away even when an inline
    // box's marker precedes it: `<card>\n <span>` does not indent the span.
    Fixture f;
    CHECK(f.css("#c { display: inline } #w { width: 500px }"));
    CHECK(f.layout("<body><div id=w><span id=c>\n  <span id=s>Welcome</span></span></div></body>"));
    const Box& s = f.tree[f.find_kind("s", BoxKind::Inline)];
    CHECK(near(s.x, 0));
}


// CSS Text L3 4.1.1. A newline kept by `pre`/`pre-wrap`/`pre-line` is a
// *segment break*, and a preserved segment break forces a line break. The
// preserved-whitespace path used to place a whole text node as one unbreakable
// fragment, so a multi-line `<pre>` laid out as a single very wide line and
// reported one line-height of height.
void test_preserved_newlines_force_line_breaks() {
    {
        // The base case: three source lines are three line boxes, and the
        // block is three line-heights tall.
        Fixture f;
        CHECK(f.css("#w { display: block; width: 400px; font-size: 16px;"
                    "     line-height: 20px; white-space: pre }"));
        CHECK(f.layout("<body><div id=w>one\ntwo\nthree</div></body>"));
        const std::vector<BoxId> ls = f.lines("w");
        CHECK(ls.size() == 3);
        CHECK_EQ(f.line_text(ls[0]), "one");
        CHECK_EQ(f.line_text(ls[1]), "two");
        CHECK_EQ(f.line_text(ls[2]), "three");
        CHECK(near(f.box("w").height, 60));
    }
    {
        // The break happens even though the whole text fits on one line, which
        // is the part a width-driven wrap can never produce.
        Fixture f;
        CHECK(f.css("#w { display: block; width: 4000px; font-size: 16px;"
                    "     line-height: 20px; white-space: pre }"));
        CHECK(f.layout("<body><div id=w>a\nb</div></body>"));
        CHECK(f.lines("w").size() == 2);
        CHECK(near(f.box("w").height, 40));
    }
    {
        // A blank line is a line: without a fragment of its own the line box is
        // dropped at flush and the block comes up one line-height short.
        Fixture f;
        CHECK(f.css("#w { display: block; width: 400px; font-size: 16px;"
                    "     line-height: 20px; white-space: pre }"));
        CHECK(f.layout("<body><div id=w>a\n\nb</div></body>"));
        CHECK(f.lines("w").size() == 3);
        CHECK(near(f.box("w").height, 60));
    }
    {
        // Inline children spanning the newlines land on their own lines — the
        // shape of weva-landing's syntax-highlighted `.code-body`, where every
        // span had been stacked onto one line at an ever-growing x.
        Fixture f;
        CHECK(f.css("#w { display: block; width: 400px; font-size: 16px;"
                    "     line-height: 20px; white-space: pre } #a, #b { display: inline }"));
        CHECK(f.layout("<body><div id=w><span id=a>one</span>\n<span id=b>two</span></div></body>"));
        const std::vector<BoxId> ls = f.lines("w");
        CHECK(ls.size() == 2);
        // One span per line, each starting at the content edge — not both
        // stacked onto one line at an ever-growing x, which is what the
        // single-fragment placement produced.
        CHECK_EQ(f.line_text(ls[0]), "one");
        CHECK_EQ(f.line_text(ls[1]), "two");
        CHECK(near(f.box("w").height, 40));
    }
    {
        // `pre-line` is the third axis: it collapses spaces and tabs the way
        // `normal` does, and still breaks at every newline.
        Fixture f;
        CHECK(f.css("#w { display: block; width: 4000px; font-size: 16px;"
                    "     line-height: 20px; white-space: pre-line }"));
        CHECK(f.layout("<body><div id=w>a   b\nc</div></body>"));
        const std::vector<BoxId> ls = f.lines("w");
        CHECK(ls.size() == 2);
        CHECK_EQ(f.line_text(ls[0]), "a b");
        CHECK_EQ(f.line_text(ls[1]), "c");
    }
    {
        // `pre-wrap` preserves the break too.
        Fixture f;
        CHECK(f.css("#w { display: block; width: 4000px; font-size: 16px;"
                    "     line-height: 20px; white-space: pre-wrap }"));
        CHECK(f.layout("<body><div id=w>a\nb</div></body>"));
        CHECK(f.lines("w").size() == 2);
    }
    {
        // And `normal` still does not: there a newline is ordinary collapsible
        // whitespace, so both words share one line.
        Fixture f;
        CHECK(f.css("#w { display: block; width: 4000px; font-size: 16px;"
                    "     line-height: 20px }"));
        CHECK(f.layout("<body><div id=w>a\nb</div></body>"));
        const std::vector<BoxId> ls = f.lines("w");
        CHECK(ls.size() == 1);
        CHECK_EQ(f.line_text(ls[0]), "a b");
    }
}


// CSS 2.1 §9.2.1.1. A block inside an inline box breaks that box, and the
// EMPTY fragments left either side of the block do not generate anonymous
// blocks. Once every line box carried a strut those phantom blocks were a full
// line-height each, so `<div><span><div>block</div></span></div>` measured
// three line-heights where Chrome and the reference both say one.
// CSS Text L3 3.1. `pre-wrap` preserves whitespace AND still wraps -- the two
// are separate axes, and treating preserved whitespace as "one unbreakable
// piece" is only right for `pre`.
//
// Every <textarea> is `pre-wrap` (Chrome's UA sheet, and ours), so while this
// was wrong not one of them soft-wrapped: a value ran off the side of the box
// and grew a horizontal scrollbar where a browser puts a second line.
void test_pre_wrap_soft_wraps() {
    {
        // Narrow enough for two words per line, and the text is preserved
        // rather than collapsed.
        Fixture f;
        CHECK(f.css("#w { display: block; width: 90px; font-size: 16px;"
                    "     line-height: 20px; white-space: pre-wrap }"));
        CHECK(f.layout("<body><div id=w>one two three four</div></body>"));
        const std::vector<BoxId> ls = f.lines("w");
        CHECK(ls.size() > 1);
    }
    {
        // `pre` in the same box does NOT wrap: one line, however long.
        Fixture f;
        CHECK(f.css("#w { display: block; width: 90px; font-size: 16px;"
                    "     line-height: 20px; white-space: pre }"));
        CHECK(f.layout("<body><div id=w>one two three four</div></body>"));
        CHECK(f.lines("w").size() == 1);
    }
    {
        // The whitespace is still preserved: a run of spaces keeps its width,
        // which is the difference from `normal`.
        Fixture f;
        CHECK(f.css("#w { display: block; width: 4000px; font-size: 16px;"
                    "     line-height: 20px; white-space: pre-wrap }"));
        CHECK(f.layout("<body><div id=w>a     b</div></body>"));
        const std::vector<BoxId> ls = f.lines("w");
        CHECK(ls.size() == 1);
        CHECK_EQ(f.line_text(ls[0]), "a     b");
    }
    {
        // Newlines still break, and each of those lines wraps on its own.
        Fixture f;
        CHECK(f.css("#w { display: block; width: 90px; font-size: 16px;"
                    "     line-height: 20px; white-space: pre-wrap }"));
        CHECK(f.layout("<body><div id=w>short\none two three four</div></body>"));
        CHECK(f.lines("w").size() > 2);
    }
}


void test_block_in_inline_empty_fragments() {
    {
        // The bare case, verified against Chrome: one line-height, not three.
        Fixture f;
        CHECK(f.css("#w { display: block; width: 400px; font-size: 16px }"
                    "#c { display: inline } #b { display: block }"));
        CHECK(f.layout("<body><div id=w><span id=c><div id=b>block</div></span></div></body>"));
        const double line = f.metrics.line_height(16);
        CHECK(near(f.box("w").height, line));
        CHECK(near(f.box("b").height, line));
        CHECK(near(f.box("b").y, 0));
    }
    {
        // The same with the source whitespace real formatting has. The
        // fragments then hold a whitespace text node rather than nothing, so
        // emptiness has to be judged by what reaches the LINE, not by the
        // fragment's child list — testing the child list left card-component
        // a line-height low.
        Fixture f;
        CHECK(f.css("#w { display: block; width: 400px; font-size: 16px }"
                    "#c { display: inline } #b { display: block }"));
        CHECK(f.layout("<body><div id=w>\n  <span id=c>\n    <div id=b>block</div>\n  </span>"
                       "\n</div></body>"));
        CHECK(near(f.box("w").height, f.metrics.line_height(16)));
    }
    {
        // Content either side of the block is NOT empty, so both fragments are
        // real lines and the container is three of them.
        Fixture f;
        CHECK(f.css("#w { display: block; width: 400px; font-size: 16px }"
                    "#c { display: inline } #b { display: block }"));
        CHECK(f.layout("<body><div id=w><span id=c>lead<div id=b>block</div>tail</span>"
                       "</div></body>"));
        CHECK(near(f.box("w").height, f.metrics.line_height(16) * 3));
    }
    {
        // An empty `<span></span>` collapses its line too (§9.4.2, and Chrome
        // agrees), so this is zero — but one that PAINTS an edge does not,
        // which is the distinction the rule actually turns on.
        Fixture f;
        CHECK(f.css("#w { display: block; width: 400px; font-size: 16px }"
                    "#c { display: inline }"));
        CHECK(f.layout("<body><div id=w><span id=c></span></div></body>"));
        CHECK(near(f.box("w").height, 0));

        Fixture g;
        CHECK(g.css("#w { display: block; width: 400px; font-size: 16px }"
                    "#c { display: inline; padding-left: 4px }"));
        CHECK(g.layout("<body><div id=w><span id=c></span></div></body>"));
        CHECK(near(g.box("w").height, g.metrics.line_height(16)));
    }
}


// Wrapping preserves trailing character spacing without adding leading spacing.
void test_letter_spacing_restarts_at_a_line_break() {
    {
        // Four characters on each line each carry four spacings.
        Fixture f;
        CHECK(f.css("#w { display: block; width: 40px; font-size: 16px;"
                    "     letter-spacing: 4px }"
                    "#b { display: inline }"));
        // "aaaa bbbb": the space gives the only break opportunity.
        CHECK(f.layout("<body><div id=w>aaaa <span id=b>bbbb</span></div></body>"));
        const std::vector<BoxId> ls = f.lines("w");
        CHECK(ls.size() == 2);
        const Box& b = f.tree[f.find_kind("b", BoxKind::Inline)];
        // The second line opens at the content edge, with no leading spacing.
        CHECK(near(b.x, 0));
        const double glyph = f.metrics.measure("bbbb", 16);
        CHECK(near(b.width, glyph + 4 * 4));
    }
    {
        // The same run unwrapped carries exactly the same four spacings, so a wide
        // container and a narrow one agree on the run's width.
        Fixture f;
        CHECK(f.css("#w { display: block; width: 4000px; font-size: 16px;"
                    "     letter-spacing: 4px }"
                    "#b { display: inline }"));
        CHECK(f.layout("<body><div id=w><span id=b>bbbb</span></div></body>"));
        const Box& b = f.tree[f.find_kind("b", BoxKind::Inline)];
        CHECK(near(b.width, f.metrics.measure("bbbb", 16) + 4 * 4));
    }
}

// Japanese and Chinese are written without spaces. A tokeniser that only
// breaks at spaces hands the whole sentence to the line as one word, and the
// line runs off the side of its box -- which is what this port did.
//
// The fixture's font is 0.5em per CODEPOINT, so at 20px every character below
// is 10px wide and the arithmetic in each case is exact.
void test_cjk_lines_break_between_characters() {
    {
        Fixture f;
        CHECK(f.css("#w { display: block; width: 40px; font-size: 20px }"));
        CHECK(f.layout("<body><div id=w>日本語テスト</div></body>"));
        const std::vector<BoxId> ls = f.lines("w");
        // Six characters at 10px in a 40px box: four, then two.
        CHECK(ls.size() == 2);
        CHECK(f.line_text(ls[0]) == "日本語テ");
        CHECK(f.line_text(ls[1]) == "スト");
    }
    {
        // Without the rule this is one line 60px wide in a 40px box. The
        // block's height is the proof that it wrapped.
        Fixture f;
        CHECK(f.css("#w { display: block; width: 40px; font-size: 20px }"));
        CHECK(f.layout("<body><div id=w>日本語テスト</div></body>"));
        CHECK(near(f.box("w").height, 2 * 20 * 1.2));
    }
    {
        // `nowrap` still forbids it: CJK is a break OPPORTUNITY, not a break.
        Fixture f;
        CHECK(f.css("#w { display: block; width: 40px; font-size: 20px; white-space: nowrap }"));
        CHECK(f.layout("<body><div id=w>日本語テスト</div></body>"));
        CHECK(f.lines("w").size() == 1);
    }
}

// Kinsoku: the prohibitions that stop a line ending or starting on the wrong
// character. Without them a Japanese paragraph breaks in places a reader
// reads as a typesetting error.
void test_cjk_kinsoku_prohibitions() {
    {
        // A full stop cannot START a line, so it stays with the character
        // before it even when that pushes both down.
        // 35px holds three characters; the piece "語。" is 20px and
        // will not fit after two, so it wraps whole.
        Fixture f;
        CHECK(f.css("#w { display: block; width: 35px; font-size: 20px }"));
        CHECK(f.layout("<body><div id=w>日本語。あ</div></body>"));
        const std::vector<BoxId> ls = f.lines("w");
        CHECK(ls.size() == 2);
        CHECK(f.line_text(ls[0]) == "日本");
        CHECK(f.line_text(ls[1]) == "語。あ");
    }
    {
        // An opening bracket cannot END one, so it goes down with what it
        // opens rather than dangling at the edge.
        Fixture f;
        CHECK(f.css("#w { display: block; width: 20px; font-size: 20px }"));
        CHECK(f.layout("<body><div id=w>あ「い</div></body>"));
        const std::vector<BoxId> ls = f.lines("w");
        CHECK(ls.size() == 2);
        CHECK(f.line_text(ls[0]) == "あ");
        CHECK(f.line_text(ls[1]) == "「い");
    }
    {
        // `line-break: loose` lifts the relaxable half: a small kana MAY start
        // a line, which is what lets a narrow column set at all.
        // Normal first: っ is small tsu, so the break before it is refused
        // and the pair moves down together.
        Fixture f;
        CHECK(f.css("#w { display: block; width: 20px; font-size: 20px }"));
        CHECK(f.layout("<body><div id=w>あいっ</div></body>"));
        const std::vector<BoxId> ls = f.lines("w");
        CHECK(ls.size() == 2);
        CHECK(f.line_text(ls[0]) == "あ");
        CHECK(f.line_text(ls[1]) == "いっ");
    }
    {
        Fixture f;
        CHECK(f.css("#w { display: block; width: 20px; font-size: 20px; line-break: loose }"));
        CHECK(f.layout("<body><div id=w>あいっ</div></body>"));
        const std::vector<BoxId> ls = f.lines("w");
        // Now every seam is a break, so two fit on the first line and the
        // small kana starts the second.
        CHECK(ls.size() == 2);
        CHECK(f.line_text(ls[0]) == "あい");
        CHECK(f.line_text(ls[1]) == "っ");
    }
}

// A break needs CJK on BOTH sides. A Latin word inside a Japanese sentence is
// still a word, and splitting it between letters would be wrong in a way no
// reader would forgive.
void test_cjk_does_not_break_latin_runs() {
    Fixture f;
    // 55px, because at 60 the whole six characters fit on one line and the
    // case tests nothing.
    CHECK(f.css("#w { display: block; width: 55px; font-size: 20px }"));
    CHECK(f.layout("<body><div id=w>日本abc語</div></body>"));
    const std::vector<BoxId> ls = f.lines("w");
    // "本abc語" is one piece: no seam inside it has CJK on both
    // sides. It is 50px, so it goes down whole rather than splitting.
    CHECK(ls.size() == 2);
    CHECK(f.line_text(ls[0]) == "日");
    CHECK(f.line_text(ls[1]) == "本abc語");
}

// `text-overflow: ellipsis` (CSS Text Overflow L3). The port read the property
// nowhere, so a fixed-width label with a long value spilled past its box -- or,
// inside a clipping one, was sliced mid-letter with no sign that anything was
// missing.
void test_text_overflow_ellipsis() {
    // 0.5em per character at 20px is 10px each, so the arithmetic below is
    // exact: 100px holds ten characters, and the ellipsis is one of them.
    const char* html = "<body><div id=w>abcdefghijklmnop</div></body>";
    {
        // Without it: the run keeps every character and overflows.
        Fixture f;
        CHECK(f.css("#w { display: block; width: 100px; font-size: 20px;"
                    "     white-space: nowrap; overflow: hidden }"));
        CHECK(f.layout(html));
        const std::vector<BoxId> ls = f.lines("w");
        CHECK(ls.size() == 1);
        CHECK(f.line_text(ls[0]) == "abcdefghijklmnop");
    }
    {
        // With it: nine characters and an ellipsis, which is ten -- exactly
        // what fits.
        Fixture f;
        CHECK(f.css("#w { display: block; width: 100px; font-size: 20px;"
                    "     white-space: nowrap; overflow: hidden;"
                    "     text-overflow: ellipsis }"));
        CHECK(f.layout(html));
        const std::vector<BoxId> ls = f.lines("w");
        CHECK(ls.size() == 1);
        CHECK(f.line_text(ls[0]) == "abcdefghi…");
    }
}

// The three conditions, each of which alone suppresses it.
void test_text_overflow_conditions() {
    const char* html = "<body><div id=w>abcdefghijklmnop</div></body>";
    const auto text_of = [&](const char* css) {
        Fixture f;
        CHECK(f.css(css));
        CHECK(f.layout(html));
        const std::vector<BoxId> ls = f.lines("w");
        return ls.empty() ? std::string() : f.line_text(ls[0]);
    };
    const std::string full = "abcdefghijklmnop";

    // A line that can WRAP does not overflow, so there is nothing to cut --
    // it becomes two lines instead.
    CHECK(text_of("#w { display: block; width: 100px; font-size: 20px; overflow: hidden;"
                  "     text-overflow: ellipsis }") != "abcdefghi…");
    // A box that does not clip lets the text spill, which is what `visible`
    // asks for.
    CHECK(text_of("#w { display: block; width: 100px; font-size: 20px; white-space: nowrap;"
                  "     overflow: visible; text-overflow: ellipsis }") == full);
    // And `clip`, the default, cuts without a mark.
    CHECK(text_of("#w { display: block; width: 100px; font-size: 20px; white-space: nowrap;"
                  "     overflow: hidden; text-overflow: clip }") == full);

    // `overflow-x` alone is enough: this is an inline-axis question.
    CHECK(text_of("#w { display: block; width: 100px; font-size: 20px; white-space: nowrap;"
                  "     overflow-x: hidden; text-overflow: ellipsis }") == "abcdefghi…");

    // Text that already fits is left alone -- no ellipsis on a short label.
    Fixture f;
    CHECK(f.css("#w { display: block; width: 300px; font-size: 20px; white-space: nowrap;"
                "     overflow: hidden; text-overflow: ellipsis }"));
    CHECK(f.layout(html));
    CHECK(f.line_text(f.lines("w")[0]) == full);
}

// CSS Lists L3 3. `li { display: list-item }` is in the user-agent
// stylesheet and nothing acted on it, so every <ul> and <ol> in the port
// rendered without a single bullet or number.
//
// The marker lives on the ITEM, not the list: an <li> is block-level, so the
// line boxes belong to it and the <ul> has none of its own.
void test_list_markers() {
    // The text of one item's first line, marker included.
    const auto item_text = [](Fixture& f, const char* id) {
        const std::vector<BoxId> ls = f.lines(id);
        return ls.empty() ? std::string("<no line>") : f.line_text(ls[0]);
    };
    {
        Fixture f;
        CHECK(f.css("li { font-size: 20px }"));
        CHECK(f.layout("<body><ul><li id=a>one</li><li id=b>two</li></ul></body>"));
        // U+2022 BULLET, before the text.
        CHECK(item_text(f, "a") == "• one");
        CHECK(item_text(f, "b") == "• two");
    }
    {
        Fixture f;
        CHECK(f.css("li { font-size: 20px; list-style-type: decimal }"));
        CHECK(f.layout("<body><ol><li id=a>a</li><li id=b>b</li><li id=c>c</li></ol></body>"));
        CHECK(item_text(f, "a") == "1. a");
        CHECK(item_text(f, "b") == "2. b");
        CHECK(item_text(f, "c") == "3. c");
    }
    {
        // `none` is how an author turns a <ul> into a plain stack, which is
        // most of the lists in a game UI.
        Fixture f;
        CHECK(f.css("li { font-size: 20px; list-style-type: none }"));
        CHECK(f.layout("<body><ul><li id=a>one</li></ul></body>"));
        CHECK(item_text(f, "a") == "one");
    }
    {
        // The other two bullet shapes.
        Fixture f;
        CHECK(f.css("#a { list-style-type: circle } #b { list-style-type: square }"));
        CHECK(f.layout("<body><ul><li id=a>x</li><li id=b>y</li></ul></body>"));
        CHECK(item_text(f, "a") == "◦ x");   // U+25E6
        CHECK(item_text(f, "b") == "▪ y");   // U+25AA
    }
}

// The HTML attributes that move the counter.
void test_list_marker_ordinals() {
    const auto mark = [](Fixture& f, const char* id) {
        const std::vector<BoxId> ls = f.lines(id);
        if (ls.empty()) return std::string("<no line>");
        const std::string t = f.line_text(ls[0]);
        return t.substr(0, t.find(' '));
    };
    {
        Fixture f;
        CHECK(f.css("li { list-style-type: decimal }"));
        CHECK(f.layout("<body><ol start=5><li id=a>a</li><li id=b>b</li></ol></body>"));
        CHECK(mark(f, "a") == "5.");
        CHECK(mark(f, "b") == "6.");
    }
    {
        // `reversed` with no `start` counts down from the number of items.
        Fixture f;
        CHECK(f.css("li { list-style-type: decimal }"));
        CHECK(f.layout("<body><ol reversed><li id=a>a</li><li id=b>b</li>"
                       "<li id=c>c</li></ol></body>"));
        CHECK(mark(f, "a") == "3.");
        CHECK(mark(f, "b") == "2.");
        CHECK(mark(f, "c") == "1.");
    }
    {
        // <li value=N> resets it, and the rest continue from there.
        Fixture f;
        CHECK(f.css("li { list-style-type: decimal }"));
        CHECK(f.layout("<body><ol><li id=a>a</li><li id=b value=10>b</li>"
                       "<li id=c>c</li></ol></body>"));
        CHECK(mark(f, "a") == "1.");
        CHECK(mark(f, "b") == "10.");
        CHECK(mark(f, "c") == "11.");
    }
    {
        Fixture f;
        CHECK(f.css("li { list-style-type: lower-roman }"));
        CHECK(f.layout("<body><ol start=4><li id=a>a</li><li id=b>b</li></ol></body>"));
        CHECK(mark(f, "a") == "iv.");
        CHECK(mark(f, "b") == "v.");
    }
    {
        // Bijective base-26: 26 is Z and 27 is AA, not BA.
        Fixture f;
        CHECK(f.css("li { list-style-type: upper-alpha }"));
        CHECK(f.layout("<body><ol start=26><li id=a>a</li><li id=b>b</li></ol></body>"));
        CHECK(mark(f, "a") == "Z.");
        CHECK(mark(f, "b") == "AA.");
    }
}

// CSS Text L3 8.1 `word-spacing`: extra space at each word separator, on top
// of the space's own advance. Unread until now, so a heading set with
// `word-spacing: 4px` came out at its natural spacing.
//
// The fixture's font is 0.5em per codepoint, so at 20px every character is
// 10px and the arithmetic below is exact.
void test_word_spacing() {
    const auto width_of = [](const char* css) {
        Fixture f;
        CHECK(f.css(css));
        CHECK(f.layout("<body><div id=w><span id=s>a b c</span></div></body>"));
        return f.tree[f.find_kind("s", BoxKind::Inline)].width;
    };
    // "a b c" is five characters: 50px with no extra spacing.
    const double plain = width_of("#w { display: block; width: 400px; font-size: 20px }");
    CHECK(near(plain, 50));

    // Two separators, so +4px each.
    const double spaced = width_of("#w { display: block; width: 400px; font-size: 20px;"
                                   "     word-spacing: 4px }");
    CHECK(near(spaced, 58));

    // `normal` is the initial value and adds nothing.
    CHECK(near(width_of("#w { display: block; width: 400px; font-size: 20px;"
                        "     word-spacing: normal }"), 50));

    // A percentage is of the font size: 10% of 20px is 2px per separator.
    CHECK(near(width_of("#w { display: block; width: 400px; font-size: 20px;"
                        "     word-spacing: 10% }"), 54));

    // It is inherited, so setting it on the block reaches the span's text --
    // which the two cases above already rely on.
}

// `word-spacing` also decides where a line breaks, because it makes the line
// wider.
void test_word_spacing_affects_wrapping() {
    // "aaa bbb" is 70px plain and fits a 80px box; with 20px of word-spacing
    // it is 90px and does not.
    Fixture f;
    CHECK(f.css("#w { display: block; width: 80px; font-size: 20px }"));
    CHECK(f.layout("<body><div id=w>aaa bbb</div></body>"));
    CHECK(f.lines("w").size() == 1);

    Fixture g;
    CHECK(g.css("#w { display: block; width: 80px; font-size: 20px; word-spacing: 20px }"));
    CHECK(g.layout("<body><div id=w>aaa bbb</div></body>"));
    CHECK(g.lines("w").size() == 2);
}

// CSS Text L3 7.1 `text-indent`: the FIRST line starts inset, and no other.
void test_text_indent() {
    {
        Fixture f;
        CHECK(f.css("#w{display:inline-block;font-size:20px;text-indent:20px}"));
        CHECK(f.layout("<body><div id=w>aaaa bbbb</div></body>"));
        CHECK(near(f.box("w").width,110)); // 9 half-em glyphs plus the indent.
        CHECK(f.lines("w").size() == 1);
    }
    // The first run's x relative to its containing block, including the line
    // offset. Checking only the child's local x concealed a doubled indent.
    // The child range is a forward-only view, so
    // this takes the first thing it yields rather than comparing iterators.
    const auto run_x = [](Fixture& f, BoxId line) {
        for (BoxId c : f.tree.children(line)) return f.tree[line].x + f.tree[c].x;
        return -1.0;
    };
    const auto first_x = [&](Fixture& f) {
        const std::vector<BoxId> ls = f.lines("w");
        return ls.empty() ? -1.0 : run_x(f, ls[0]);
    };
    {
        Fixture f;
        CHECK(f.css("#w { display: block; width: 200px; font-size: 20px }"));
        CHECK(f.layout("<body><div id=w>aaaa bbbb cccc dddd</div></body>"));
        CHECK(near(first_x(f), 0));
    }
    {
        // Indented: the first run starts 40px in.
        Fixture f;
        CHECK(f.css("#w { display: block; width: 200px; font-size: 20px; text-indent: 40px }"));
        CHECK(f.layout("<body><div id=w>aaaa bbbb cccc dddd</div></body>"));
        CHECK(near(first_x(f), 40));
        // And the SECOND line is not indented, which is the whole point of
        // the property being first-line only.
        const std::vector<BoxId> ls = f.lines("w");
        CHECK(ls.size() >= 2);
        if (ls.size() >= 2) CHECK(near(run_x(f, ls[1]), 0));
    }
    {
        // A percentage is of the containing block's width: 10% of 200 is 20.
        Fixture f;
        CHECK(f.css("#w { display: block; width: 200px; font-size: 20px; text-indent: 10% }"));
        CHECK(f.layout("<body><div id=w>aaaa bbbb cccc dddd</div></body>"));
        CHECK(near(first_x(f), 20));
    }
    {
        // The `hanging` and `each-line` keywords are accepted and ignored;
        // the length before them is still honoured rather than the whole
        // declaration being dropped.
        Fixture f;
        CHECK(f.css("#w { display: block; width: 200px; font-size: 20px;"
                    "     text-indent: 30px hanging }"));
        CHECK(f.layout("<body><div id=w>aaaa bbbb cccc dddd</div></body>"));
        CHECK(near(first_x(f), 30));
    }
}

// CSS Text L3 5.5 `overflow-wrap: break-word`. The port read neither spelling
// of it, so the value authors actually write to stop a long name blowing out
// a panel did nothing at all.
//
// It is NOT `break-all`: a word is kept whole and moved to the next line as
// usual, and broken only when it is alone on a line and still does not fit.
void test_overflow_wrap_break_word() {
    // 0.5em per character at 20px, so a 100px box holds ten.
    const char* html = "<body><div id=w>ab supercalifragilistic</div></body>";
    {
        // Without it, the long word moves to its own line and overflows.
        Fixture f;
        CHECK(f.css("#w { display: block; width: 100px; font-size: 20px }"));
        CHECK(f.layout(html));
        const std::vector<BoxId> ls = f.lines("w");
        CHECK(ls.size() == 2);
        if (ls.size() == 2) CHECK(f.line_text(ls[1]) == "supercalifragilistic");
    }
    {
        // With it, the word that cannot fit a line of its own is split.
        Fixture f;
        CHECK(f.css("#w { display: block; width: 100px; font-size: 20px;"
                    "     overflow-wrap: break-word }"));
        CHECK(f.layout(html));
        const std::vector<BoxId> ls = f.lines("w");
        CHECK(ls.size() > 2);
        // Every line after the first fits, which is the whole point.
        for (std::size_t i = 1; i < ls.size(); ++i) {
            CHECK(f.tree[ls[i]].width <= 100 + 0.01);
        }
    }
    {
        // `word-wrap` is the older name for the same thing and is still what
        // most stylesheets say.
        Fixture f;
        CHECK(f.css("#w { display: block; width: 100px; font-size: 20px;"
                    "     word-wrap: break-word }"));
        CHECK(f.layout(html));
        CHECK(f.lines("w").size() > 2);
    }
}

// What break-word must NOT do, which is what separates it from break-all.
void test_break_word_keeps_words_whole_when_they_fit() {
    // "aaaa bbbb" at 20px is 40px each. In a 60px box, break-all would split
    // the first word across the line end; break-word moves it down whole.
    const char* html = "<body><div id=w>aaaa bbbb</div></body>";
    {
        Fixture f;
        CHECK(f.css("#w { display: block; width: 60px; font-size: 20px;"
                    "     overflow-wrap: break-word }"));
        CHECK(f.layout(html));
        const std::vector<BoxId> ls = f.lines("w");
        CHECK(ls.size() == 2);
        if (ls.size() == 2) {
            CHECK(f.line_text(ls[0]) == "aaaa");
            CHECK(f.line_text(ls[1]) == "bbbb");
        }
    }
    {
        // break-all does split it, which is the difference.
        Fixture f;
        CHECK(f.css("#w { display: block; width: 60px; font-size: 20px;"
                    "     word-break: break-all }"));
        CHECK(f.layout(html));
        const std::vector<BoxId> ls = f.lines("w");
        CHECK(ls.size() >= 2);
        if (!ls.empty()) CHECK(f.line_text(ls[0]) != "aaaa");
    }
}

// CSS Text L3 7.2 `tab-size`. A tab in preserved text had no width control at
// all: it was measured as whatever the face gives it, which for the built-in
// one is nothing, so a code listing in a <pre> lost every level of its
// indentation.
//
// The fixture's font is 0.5em per character, so at 20px a space is 10px and a
// default 8-space tab stop is 80px.
void test_tab_size() {
    // How far the RUNS reach, not how wide the line box is: a line box in a
    // 400px block is 400px wide whatever it holds.
    const auto width_of = [](const char* css, const char* html) {
        Fixture f;
        CHECK(f.css(css));
        CHECK(f.layout(html));
        const std::vector<BoxId> ls = f.lines("w");
        if (ls.empty()) return -1.0;
        double reach = 0;
        for (BoxId c : f.tree.children(ls[0])) {
            reach = std::max(reach, f.tree[c].x + f.tree[c].width);
        }
        return reach;
    };
    // One leading tab, then a letter. The tab reaches the first stop at 80px
    // and the letter follows, so the line is 90px.
    const char* html = "<body><pre id=w>	x</pre></body>";
    CHECK(near(width_of("#w { display: block; width: 400px; font-size: 20px;"
                        "     white-space: pre }", html), 90));

    for (const char* zero : {"0", "0px", "0em"}) {
        const std::string css = std::string("#w { display:block;width:400px;font-size:20px;white-space:pre;tab-size:") + zero + "}";
        CHECK(near(width_of(css.c_str(), html), 10));
    }
    for (const char* ws : {"pre", "pre-wrap"}) {
        for (const char* size : {"25px", "2.5"}) {
            const std::string css = std::string("#w {display:block;width:400px;font-size:20px;white-space:") + ws + ";tab-size:" + size + "}";
            CHECK(near(width_of(css.c_str(), html), 35));
            CHECK(near(width_of(css.c_str(), "<body><pre id=w>ab\tx</pre></body>"), 35));
        }
    }
    // `tab-size: 4` puts the stop at 40px.
    CHECK(near(width_of("#w {display:block;width:400px;font-size:20px;white-space:pre;tab-size:4}",
        "<body><pre id=w><span style='font-size:40px'>\tx</span></pre></body>"), 60));
    CHECK(near(width_of("#w {display:block;width:400px;font-size:20px;white-space:pre;tab-size:4}",
        "<body><pre id=w><span style='word-spacing:5px'>\tx</span></pre></body>"), 50));
    CHECK(near(width_of("#w { display: block; width: 400px; font-size: 20px;"
                        "     white-space: pre; tab-size: 4 }", html), 50));

    // A tab STOPS at a multiple, it does not add a fixed width: two tabs from
    // zero reach 40 then 80 at tab-size 4, not 40 and 80 either way -- so the
    // difference shows with text between them.
    const char* mixed = "<body><pre id=w>ab	x</pre></body>";
    // "ab" is 20px; the tab reaches 40, then "x" -> 50.
    CHECK(near(width_of("#w { display: block; width: 400px; font-size: 20px;"
                        "     white-space: pre; tab-size: 4 }", mixed), 50));

    // A length resolves through the space's width: 20px is two spaces here.
    CHECK(near(width_of("#w { display: block; width: 400px; font-size: 20px;"
                        "     white-space: pre; tab-size: 20px }", html), 30));

    // And `pre-wrap` gets the same treatment, since it preserves tabs too.
    CHECK(near(width_of("#w { display: block; width: 400px; font-size: 20px;"
                        "     white-space: pre-wrap; tab-size: 4 }", html), 50));
}

// The tab must not survive into the painted text: layout and paint each
// measure what they are given, so a fragment holding a raw tab would draw its
// glyphs somewhere layout did not put them.
void test_tabs_are_expanded_not_measured() {
    Fixture f;
    CHECK(f.css("#w { display: block; width: 400px; font-size: 20px;"
                "     white-space: pre; tab-size: 4 }"));
    CHECK(f.layout("<body><pre id=w>	x</pre></body>"));
    const std::vector<BoxId> ls = f.lines("w");
    CHECK(!ls.empty());
    if (!ls.empty()) {
        const std::string text = f.line_text(ls[0]);
        CHECK(text.find('	') == std::string::npos);
        CHECK(text == " x");
        for (BoxId c : f.tree.children(ls[0])) {
            if (f.tree[c].text == "x") CHECK(near(f.tree[c].x, 40));
        }
    }
}

// A marker must not take the item's own box properties. The reference warns
// about exactly this: styling the marker with the li's ComputedStyle hands it
// the li's padding, border and background, and its `.zebra li` measured 26.9
// where Chrome says 26.
void test_list_marker_does_not_inherit_the_items_box() {
    Fixture f;
    CHECK(f.css("li { font-size: 20px; padding: 10px; border: 2px solid #000;"
                "     list-style-type: decimal }"));
    CHECK(f.layout("<body><ol><li id=a>x</li></ol></body>"));
    const std::vector<BoxId> ls = f.lines("a");
    CHECK(!ls.empty());
    if (ls.empty()) return;
    // The marker is measured from the item's CONTENT edge -- border 2 plus
    // padding 10 -- and not one padding further in. That is what this test is
    // about, and it still holds; what changed is which side of the edge the
    // marker lands on.
    //
    // `list-style-position` is `outside` by default, so the marker ENDS at the
    // content edge rather than starting there. Asserting x=0 here was
    // asserting the marker sat in the inline flow, which shifted every inline
    // child of every list item right by the marker's width.
    double first_x = 0, last_x = 0;
    bool any = false;
    for (BoxId c : f.tree.children(ls[0])) {
        if (!any) first_x = f.tree[c].x;
        last_x = f.tree[c].x;
        any = true;
    }
    CHECK(any);
    // Relative to the line box, which is already inset by the frame.
    CHECK(first_x < 0);
    // And the item's own content begins exactly at the edge.
    CHECK(near(last_x, 0));

    // And the item is one line tall plus its own frame: a marker carrying the
    // li's padding a second time would show up here.
    CHECK(near(f.box("a").height, 20 * 1.2 + 20 + 4));
}

// `li::marker { ... }` styles the marker apart from the item, which is the
// only way to give a bullet its own colour or size. The port computed no
// marker pseudo at all, so such a rule matched nothing.
void test_marker_pseudo_styles_the_marker() {
    // The marker is the first run on the item's line. Its FONT SIZE is the
    // thing ::marker changes, and asserting on that rather than on a width
    // keeps the test off the tokeniser's split of "1." and the space after it.
    const auto marker_font = [](Fixture& f) {
        const std::vector<BoxId> ls = f.lines("a");
        if (ls.empty()) return -1.0;
        for (BoxId c : f.tree.children(ls[0])) return f.tree[c].font_size;
        return -1.0;
    };
    {
        // No rule: the marker keeps the item's own size.
        Fixture f;
        CHECK(f.css("li { font-size: 30px; list-style-type: decimal }"));
        CHECK(f.layout("<body><ol><li id=a>x</li></ol></body>"));
        CHECK(near(marker_font(f), 30));
    }
    {
        // A rule: the marker takes its own, and the item keeps its.
        Fixture f;
        CHECK(f.css("li { font-size: 10px; list-style-type: decimal }"
                    "li::marker { font-size: 30px }"));
        CHECK(f.layout("<body><ol><li id=a>x</li></ol></body>"));
        CHECK(near(marker_font(f), 30));
        // The item's own text is still 10px, which is what "apart from the
        // item" means.
        double last = -1;
        for (BoxId c : f.tree.children(f.lines("a")[0])) last = f.tree[c].font_size;
        CHECK(near(last, 10));
    }
}

// The expansion has to reach LAYOUT, not just the expander: a shorthand that
// produces the right longhands and is then dropped somewhere between is the
// same bug from the author's side.
void test_flex_flow_reaches_layout() {
    {
        // Row, the default: two items side by side.
        Fixture f;
        CHECK(f.css("#g { display: flex; width: 300px }"
                    ".i { width: 50px; height: 20px }"));
        CHECK(f.layout("<body><div id=g><div id=a class=i></div>"
                       "<div id=b class=i></div></div></body>"));
        CHECK(near(f.box("b").x, 50));
        CHECK(near(f.box("b").y, 0));
    }
    {
        // `flex-flow: column` stacks them, through the shorthand alone.
        Fixture f;
        CHECK(f.css("#g { display: flex; flex-flow: column; width: 300px }"
                    ".i { width: 50px; height: 20px }"));
        CHECK(f.layout("<body><div id=g><div id=a class=i></div>"
                       "<div id=b class=i></div></div></body>"));
        CHECK(near(f.box("b").x, 0));
        CHECK(near(f.box("b").y, 20));
    }
    {
        // And the wrap half: three 50px items in a 120px row wrap to a second
        // line only when the shorthand's `wrap` arrives.
        Fixture f;
        CHECK(f.css("#g { display: flex; flex-flow: row wrap; width: 120px }"
                    ".i { width: 50px; height: 20px; flex: none }"));
        CHECK(f.layout("<body><div id=g><div id=a class=i></div><div id=b class=i></div>"
                       "<div id=c class=i></div></div></body>"));
        CHECK(near(f.box("c").y, 20));   // pushed to the second line
    }
}

void test_inline_wrapped_fragments_keep_element_identity() {
    Fixture f;
    CHECK(f.css("#w{width:50px;font-size:20px}#s{background:red}"));
    CHECK(f.layout("<div id=w><span id=s>one two three</span></div>"));
    const auto lines = f.lines("w");
    CHECK(lines.size() == 3);
    for (BoxId line : lines) {
        const BoxId fragment = f.find_kind("s", BoxKind::Inline, line);
        CHECK(fragment != kNoBox);
        if (fragment != kNoBox) {
            CHECK(f.tree[fragment].width > 0);
            CHECK(near(f.tree[fragment].height, 24));
            CHECK(f.tree[fragment].element == f.tree[f.find_kind("s", BoxKind::Inline)].element);
        }
    }
}
