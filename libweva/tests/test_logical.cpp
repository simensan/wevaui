#include "check.h"
#include "weva/cascade.h"
#include "weva/dom.h"
#include "weva/html.h"
#include "weva/logical.h"
#include <memory>
#include <string>

using namespace weva;

namespace {

struct Fixture {
    SymbolTable symbols;
    Ref<Document> doc;
    std::vector<std::unique_ptr<Stylesheet>> sheets;
    CascadeEngine engine;
    NullStateProvider state;

    bool html(std::string_view h) {
        HtmlParseError e;
        ParseOptions o;
        o.strict = false;
        doc = parse_html(h, &symbols, o, &e);
        return static_cast<bool>(doc);
    }
    bool css(std::string_view c) {
        auto s = std::make_unique<Stylesheet>();
        CssParseError e;
        if (!parse_stylesheet(c, false, s.get(), &e)) return false;
        engine.add_stylesheet(s.get(), DeclarationOrigin::Author);
        sheets.push_back(std::move(s));
        return true;
    }
    Element* id(std::string_view s) { return doc->get_element_by_id(s); }
    std::string value(std::string_view element_id, std::string_view prop) {
        Element* e = id(element_id);
        if (!e) return "<no element>";
        ComputedStyle cs;
        engine.compute(*e, state, nullptr, &cs);
        return std::string(cs.get(prop));
    }
    // Computes `parent_id` first and feeds it in as the inheritance source, so
    // an inherited `direction`/`writing-mode` actually reaches the child.
    std::string value_under(std::string_view parent_id, std::string_view child_id,
                            std::string_view prop) {
        Element* p = id(parent_id);
        Element* c = id(child_id);
        if (!p || !c) return "<no element>";
        ComputedStyle ps;
        engine.compute(*p, state, nullptr, &ps);
        ComputedStyle cs;
        engine.compute(*c, state, &ps, &cs);
        return std::string(cs.get(prop));
    }
};

} // namespace

void test_logical_axes() {
    // ---- horizontal-tb: the familiar mapping, mirrored by `direction`
    LogicalAxes a = LogicalAxes::from("ltr", "horizontal-tb");
    CHECK(a.inline_start == "left" && a.inline_end == "right");
    CHECK(a.block_start == "top" && a.block_end == "bottom");
    CHECK(a.inline_is_horizontal);

    a = LogicalAxes::from("rtl", "horizontal-tb");
    CHECK(a.inline_start == "right" && a.inline_end == "left");
    CHECK(a.block_start == "top" && a.block_end == "bottom");
    CHECK(a.inline_is_horizontal);

    // ---- vertical modes rotate BOTH axes: the inline axis becomes vertical,
    // so inline-size is a height, and the block axis runs left/right.
    a = LogicalAxes::from("ltr", "vertical-rl");
    CHECK(a.inline_start == "top" && a.inline_end == "bottom");
    CHECK(a.block_start == "right" && a.block_end == "left");
    CHECK(!a.inline_is_horizontal);

    a = LogicalAxes::from("ltr", "vertical-lr");
    CHECK(a.inline_start == "top" && a.inline_end == "bottom");
    CHECK(a.block_start == "left" && a.block_end == "right");
    CHECK(!a.inline_is_horizontal);

    // sideways-rl shares vertical-rl's block axis; sideways-lr flips the
    // inline direction relative to vertical-lr even though its block axis is
    // the same, because the glyphs rotate the other way.
    a = LogicalAxes::from("ltr", "sideways-rl");
    CHECK(a.inline_start == "top" && a.block_start == "right");
    a = LogicalAxes::from("ltr", "sideways-lr");
    CHECK(a.inline_start == "bottom" && a.inline_end == "top");
    CHECK(a.block_start == "left" && a.block_end == "right");

    a = LogicalAxes::from("rtl", "sideways-lr");
    CHECK(a.inline_start == "top" && a.inline_end == "bottom");

    // ---- casing and unknown keywords fall back to horizontal-tb
    a = LogicalAxes::from("RTL", "Vertical-RL");
    CHECK(a.inline_start == "bottom" && a.block_start == "right");
    a = LogicalAxes::from("ltr", "nonsense");
    CHECK(a.inline_start == "left" && a.inline_is_horizontal);
}

void test_logical_in_cascade() {
    {
        Fixture f;
        CHECK(f.html("<div id=a></div><div id=b dir=rtl></div>"));
        CHECK(f.css("#a { margin-inline-start: 10px; padding-inline-end: 3px;"
                    "     inset-inline-start: 1px; border-inline-start-width: 2px }"
                    "#b { direction: rtl; margin-inline-start: 10px;"
                    "     inset-inline-start: 1px }"));
        // ltr: inline-start is the left edge.
        CHECK_EQ(f.value("a", "margin-left"), "10px");
        CHECK_EQ(f.value("a", "padding-right"), "3px");
        CHECK_EQ(f.value("a", "left"), "1px");
        CHECK_EQ(f.value("a", "border-left-width"), "2px");
        // The logical property itself keeps its value; layout only reads the
        // physical one, but nothing erases the source declaration.
        CHECK_EQ(f.value("a", "margin-right"), "0");   // the registered initial is bare `0`, not `0px`

        // rtl mirrors the inline axis and leaves the block axis alone.
        CHECK_EQ(f.value("b", "margin-right"), "10px");
        CHECK_EQ(f.value("b", "margin-left"), "0");
        CHECK_EQ(f.value("b", "right"), "1px");
    }
    {
        // ---- vertical writing mode: inline-size is a HEIGHT.
        Fixture f;
        CHECK(f.html("<div id=a></div>"));
        CHECK(f.css("#a { writing-mode: vertical-rl; inline-size: 50px;"
                    "     block-size: 20px; max-inline-size: 80px;"
                    "     margin-block-start: 4px; padding-inline-end: 6px;"
                    "     border-start-end-radius: 9px }"));
        CHECK_EQ(f.value("a", "height"), "50px");
        CHECK_EQ(f.value("a", "width"), "20px");
        CHECK_EQ(f.value("a", "max-height"), "80px");
        // block-start is the right edge in vertical-rl.
        CHECK_EQ(f.value("a", "margin-right"), "4px");
        // inline-end is the bottom edge.
        CHECK_EQ(f.value("a", "padding-bottom"), "6px");
        // block-start(right) + inline-end(bottom) = the bottom-right corner.
        CHECK_EQ(f.value("a", "border-bottom-right-radius"), "9px");
    }
    {
        // ---- direction/writing-mode are INHERITED, so the axes must be read
        // through the inherit chain, not from local declarations only.
        Fixture f;
        CHECK(f.html("<div id=p><div id=c></div></div>"));
        CHECK(f.css("#p { direction: rtl } #c { margin-inline-start: 5px }"));
        CHECK_EQ(f.value_under("p", "c", "margin-right"), "5px");
        CHECK_EQ(f.value_under("p", "c", "margin-left"), "0");
        // Computed standalone, with no parent, the same element is ltr.
        CHECK_EQ(f.value("c", "margin-left"), "5px");
    }
}

void test_logical_vs_physical_order() {
    {
        // A logical alias is NOT a fixed loser to the physical property: it
        // carries the logical declaration's own cascade key, so ordinary
        // source order decides.
        Fixture f;
        CHECK(f.html("<div id=a></div>"));
        CHECK(f.css("#a { margin-left: 1px; margin-inline-start: 2px }"));
        CHECK_EQ(f.value("a", "margin-left"), "2px");
    }
    {
        Fixture f;
        CHECK(f.html("<div id=a></div>"));
        CHECK(f.css("#a { margin-inline-start: 2px; margin-left: 1px }"));
        CHECK_EQ(f.value("a", "margin-left"), "1px");
    }
    {
        // Specificity outranks source order, in both directions.
        Fixture f;
        CHECK(f.html("<div id=a class=x></div>"));
        CHECK(f.css("#a { margin-left: 1px } .x { margin-inline-start: 2px }"));
        CHECK_EQ(f.value("a", "margin-left"), "1px");
    }
    {
        Fixture f;
        CHECK(f.html("<div id=a class=x></div>"));
        CHECK(f.css(".x { margin-left: 1px } #a { margin-inline-start: 2px }"));
        CHECK_EQ(f.value("a", "margin-left"), "2px");
    }
    {
        // !important on the logical property beats a later normal physical one.
        Fixture f;
        CHECK(f.html("<div id=a></div>"));
        CHECK(f.css("#a { margin-inline-start: 2px !important; margin-left: 1px }"));
        CHECK_EQ(f.value("a", "margin-left"), "2px");
    }
    {
        // ...and the importance rides along, so a still-later normal
        // declaration cannot take the slot back either.
        Fixture f;
        CHECK(f.html("<div id=a style=\"margin-left: 7px\"></div>"));
        CHECK(f.css("#a { margin-inline-start: 2px !important }"));
        CHECK_EQ(f.value("a", "margin-left"), "2px");
    }
    {
        // An inline physical declaration beats a stylesheet logical one.
        Fixture f;
        CHECK(f.html("<div id=a style=\"margin-left: 7px\"></div>"));
        CHECK(f.css("#a { margin-inline-start: 2px }"));
        CHECK_EQ(f.value("a", "margin-left"), "7px");
    }
    {
        // An inline LOGICAL declaration beats a stylesheet physical one.
        Fixture f;
        CHECK(f.html("<div id=a style=\"margin-inline-start: 7px\"></div>"));
        CHECK(f.css("#a { margin-left: 2px }"));
        CHECK_EQ(f.value("a", "margin-left"), "7px");
    }
}

void test_logical_before_substitution() {
    // The mapping runs before var()/env()/attr(), so the substituted value
    // lands on the physical property and is resolved exactly once.
    Fixture f;
    CHECK(f.html("<div id=a data-w='30px'></div>"));
    CHECK(f.css("#a { --gap: 7px; margin-inline-start: var(--gap);"
                "     inline-size: attr(data-w length);"
                "     padding-block-start: calc(2px + var(--gap)) }"));
    CHECK_EQ(f.value("a", "margin-left"), "7px");
    CHECK_EQ(f.value("a", "width"), "30px");
    CHECK_EQ(f.value("a", "padding-top"), "calc(2px + 7px)");

    // A logical declaration that is invalid at computed-value time drops the
    // PHYSICAL slot too — the alias copied the unresolvable text across, so
    // the same var() failure has to reach it.
    Fixture g;
    CHECK(g.html("<div id=a></div>"));
    CHECK(g.css("#a { margin-inline-start: var(--missing) }"));
    CHECK_EQ(g.value("a", "margin-left"), "0");
}
