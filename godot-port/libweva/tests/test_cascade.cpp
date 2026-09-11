#include "check.h"
#include "weva/cascade.h"
#include "weva/dom.h"
#include "weva/html.h"
#include "weva/user_agent_stylesheet.h"
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
    bool css(std::string_view c, DeclarationOrigin origin = DeclarationOrigin::Author) {
        auto s = std::make_unique<Stylesheet>();
        CssParseError e;
        if (!parse_stylesheet(c, false, s.get(), &e)) return false;
        engine.add_stylesheet(s.get(), origin);
        sheets.push_back(std::move(s));
        return true;
    }
    Element* id(std::string_view s) { return doc->get_element_by_id(s); }
    std::string value(std::string_view element_id, std::string_view prop) {
        ComputedStyle cs;
        engine.compute(*id(element_id), state, nullptr, &cs);
        return std::string(cs.get(prop));
    }
};

MatchedDeclaration make(const Declaration* d, DeclarationOrigin o, Specificity s,
                        int src, bool inl = false, int layer = kUnlayeredOrdinal) {
    MatchedDeclaration m;
    m.declaration = d;
    m.origin = o;
    m.specificity = s;
    m.source_index = src;
    m.is_inline = inl;
    m.layer_ordinal = layer;
    return m;
}

} // namespace

void test_cascade_order() {
    // @supports selector(): a selector the engine parses is supported, one it
    // does not is not, and a `:` inside it is not a declaration's colon.
    for (const auto& [rule, expected] : std::vector<std::pair<const char*, const char*>>{
             {"@supports selector(#a:hover) { #a { color: red } }", "red"},
             {"@supports selector(:is(#a, .b) > span) { #a { color: red } }", "red"},
             {"@supports not selector(#a:hover) { #a { color: red } }", "green"},
             {"@supports selector(#a:::bogus) { #a { color: red } }", "green"},
             {"@supports (color: red) and selector(#a) { #a { color: red } }", "red"}}) {
        Fixture f;
        CHECK(f.html("<from id=a>x</from>"));
        CHECK(f.css(std::string("from { color: green }") + rule));
        CHECK(f.value("a", "color") == expected);
    }
    // Unknown blocks must not promote their contents to ordinary rules.
    // The same applies to non-grouping rules such as animation keyframes.
    for (const char* rule : {
             "@unknown { #a { color: red } }",
             "@unknown { @media all { #a { color: red } } }",
             "@media all { @unknown { #a { color: red } } }",
             "@supports (display: block) { @unknown { #a { color: red } } }",
             "@layer theme { @unknown { #a { color: red !important } } }",
             "@keyframes fade { from { color: red } to { color: blue } }"}) {
        Fixture f;
        CHECK(f.html("<from id=a>x</from>"));
        CHECK(f.css(std::string("from { color: green }") + rule));
        CHECK(f.value("a", "color") == "green");
        // Adding a later valid sheet must invalidate cached matches normally.
        CHECK(f.css("@media all { #a { color: blue } }"));
        CHECK(f.value("a", "color") == "blue");
    }
    Declaration normal{"color", "red", false};
    Declaration important{"color", "blue", true};

    // ---- !important is the dominant axis
    {
        auto a = make(&normal, DeclarationOrigin::Author, {1, 0, 0}, 99);
        auto b = make(&important, DeclarationOrigin::UserAgent, {0, 0, 0}, 0);
        CHECK(compare_for_cascade(a, b) < 0);
    }
    // ---- normal origin order is UA < User < Author
    {
        auto ua = make(&normal, DeclarationOrigin::UserAgent, {1, 0, 0}, 0);
        auto author = make(&normal, DeclarationOrigin::Author, {0, 0, 0}, 0);
        CHECK(compare_for_cascade(ua, author) < 0);
    }
    // ---- !important REVERSES it: Author < User < UA
    {
        auto ua = make(&important, DeclarationOrigin::UserAgent, {0, 0, 0}, 0);
        auto author = make(&important, DeclarationOrigin::Author, {1, 0, 0}, 0);
        CHECK(compare_for_cascade(author, ua) < 0);
    }
    // ---- specificity, then source order, then in-rule order
    {
        auto lo = make(&normal, DeclarationOrigin::Author, {0, 1, 0}, 0);
        auto hi = make(&normal, DeclarationOrigin::Author, {1, 0, 0}, 0);
        CHECK(compare_for_cascade(lo, hi) < 0);
        auto first = make(&normal, DeclarationOrigin::Author, {0, 1, 0}, 0);
        auto later = make(&normal, DeclarationOrigin::Author, {0, 1, 0}, 5);
        CHECK(compare_for_cascade(first, later) < 0);
        auto d0 = make(&normal, DeclarationOrigin::Author, {0, 1, 0}, 3);
        auto d1 = make(&normal, DeclarationOrigin::Author, {0, 1, 0}, 3);
        d1.in_rule_index = 1;
        CHECK(compare_for_cascade(d0, d1) < 0);
    }
    // ---- inline beats any selector for NORMAL declarations
    {
        auto sel = make(&normal, DeclarationOrigin::Author, {9, 9, 9}, 0);
        auto inl = make(&normal, DeclarationOrigin::Author, {0, 0, 0}, 0, true);
        CHECK(compare_for_cascade(sel, inl) < 0);
    }
    // ---- layer axis, normal: LATER layer wins; unlayered beats all layers
    {
        auto early = make(&normal, DeclarationOrigin::Author, {0, 1, 0}, 0, false, 1);
        auto late = make(&normal, DeclarationOrigin::Author, {0, 1, 0}, 0, false, 2);
        CHECK(compare_for_cascade(early, late) < 0);
        auto unlayered = make(&normal, DeclarationOrigin::Author, {0, 0, 0}, 0);
        CHECK(compare_for_cascade(late, unlayered) < 0);
    }
    // ---- layer axis, !important: REVERSED — EARLIER layer wins, unlayered LOSES
    {
        auto early = make(&important, DeclarationOrigin::Author, {0, 1, 0}, 0, false, 1);
        auto late = make(&important, DeclarationOrigin::Author, {0, 1, 0}, 0, false, 2);
        CHECK(compare_for_cascade(late, early) < 0);
        auto unlayered = make(&important, DeclarationOrigin::Author, {9, 9, 9}, 0);
        CHECK(compare_for_cascade(unlayered, late) < 0);
    }
    // ---- and for !important the layer axis applies EVEN TO INLINE, which is
    // why the comparison must not be skipped when one side is inline.
    {
        auto inline_imp = make(&important, DeclarationOrigin::Author, {0, 0, 0}, 0, true);
        auto layered_imp = make(&important, DeclarationOrigin::Author, {0, 0, 0}, 0, false, 3);
        CHECK(compare_for_cascade(inline_imp, layered_imp) < 0);
    }
}

void test_cascade_compute() {
    {
        Fixture f;
        CHECK(f.html("<div id=p><button id=b>label</button><input id=i><select id=s></select>"
                     "<textarea id=t></textarea></div>"));
        CHECK(f.css(user_agent_stylesheet_source(), DeclarationOrigin::UserAgent));
        CHECK(f.css("#p{letter-spacing:3px;word-spacing:4px;text-transform:uppercase;"
                    "text-indent:5px;text-shadow:1px 1px red}"));
        ComputedStyle parent;
        f.engine.compute(*f.id("p"),f.state,nullptr,&parent);
        for (const char* id : {"b","i","s","t"}) {
            ComputedStyle child;
            f.engine.compute(*f.id(id),f.state,&parent,&child);
            CHECK(child.get("letter-spacing") == "normal");
            CHECK(child.get("word-spacing") == "normal");
            CHECK(child.get("text-transform") == "none");
            CHECK(child.get("text-indent") == "0");
            CHECK(child.get("text-shadow") == "none");
        }
        CHECK(f.css("button,input,select,textarea{letter-spacing:inherit;word-spacing:inherit;"
                    "text-transform:inherit;text-indent:inherit;text-shadow:inherit}"));
        for (const char* id : {"b","i","s","t"}) {
            ComputedStyle child;
            f.engine.compute(*f.id(id),f.state,&parent,&child);
            for (const char* property : {"letter-spacing","word-spacing","text-transform",
                                          "text-indent","text-shadow"})
                CHECK(child.get(property) == parent.get(property));
        }
    }
    // ---- specificity resolves competing rules
    {
        Fixture f;
        CHECK(f.html("<div id=a class=box>x</div>"));
        CHECK(f.css("div { color: red } .box { color: green } #a { color: blue }"));
        CHECK(f.value("a", "color") == "blue");
    }
    // ---- source order breaks a specificity tie
    {
        Fixture f;
        CHECK(f.html("<div id=a class=box>x</div>"));
        CHECK(f.css(".box { color: red } .box { color: green }"));
        CHECK(f.value("a", "color") == "green");
    }
    // ---- !important overrides a more specific normal rule
    {
        Fixture f;
        CHECK(f.html("<div id=a class=box>x</div>"));
        CHECK(f.css(".box { color: red !important } #a { color: blue }"));
        CHECK(f.value("a", "color") == "red");
    }
    // ---- author beats UA for normal; UA beats author for !important
    {
        Fixture f;
        CHECK(f.html("<div id=a>x</div>"));
        CHECK(f.css("div { color: gray }", DeclarationOrigin::UserAgent));
        CHECK(f.css("div { color: black }", DeclarationOrigin::Author));
        CHECK(f.value("a", "color") == "black");

        Fixture g;
        CHECK(g.html("<div id=a>x</div>"));
        CHECK(g.css("div { color: gray !important }", DeclarationOrigin::UserAgent));
        CHECK(g.css("div { color: black !important }", DeclarationOrigin::Author));
        CHECK(g.value("a", "color") == "gray");
    }
    // ---- inline beats selectors; !important beats inline
    {
        Fixture f;
        CHECK(f.html("<div id=a style='color: purple'>x</div>"));
        CHECK(f.css("#a { color: blue }"));
        CHECK(f.value("a", "color") == "purple");

        Fixture g;
        CHECK(g.html("<div id=a style='color: purple'>x</div>"));
        CHECK(g.css("#a { color: blue !important }"));
        CHECK(g.value("a", "color") == "blue");
    }
    // ---- a shorthand in an inline style expands, like one in a rule
    {
        // Stylesheet rules are expanded once at compile time; inline styles
        // reached the cascade unexpanded, so `style="margin: 0"` set a `margin`
        // slot nothing reads while the UA sheet's already-expanded
        // `p { margin: 1em 0 }` longhands kept the element. Every
        // `<p style="margin:0">` in the corpus sat 16px too low.
        Fixture f;
        CHECK(f.html("<div id=a style='margin: 0'>x</div>"));
        CHECK(f.css("#a { margin-top: 7px; margin-left: 7px }"));
        CHECK(f.value("a", "margin-top") == "0");
        CHECK(f.value("a", "margin-left") == "0");

        // Source order still decides between an inline longhand and the
        // expansion of an inline shorthand, which is the whole reason
        // expansion happens at cascade time rather than at read time.
        Fixture g;
        CHECK(g.html("<div id=a style='margin: 1px; margin-left: 9px'>x</div>"));
        CHECK(g.value("a", "margin-left") == "9px");
        CHECK(g.value("a", "margin-top") == "1px");

        Fixture h;
        CHECK(h.html("<div id=a style='margin-left: 9px; margin: 1px'>x</div>"));
        CHECK(h.value("a", "margin-left") == "1px");
    }
    // ---- the parsed-value memo tracks the string it describes
    {
        // A memo that outlives its value is worse than no memo. These pin the
        // invalidation, because the win it buys (2.1x on a layout pass) is only
        // safe if a rewritten slot re-parses.
        Fixture f;
        CHECK(f.html("<div id=a>x</div>"));
        CHECK(f.css("#a { width: 10px }"));
        ComputedStyle cs;
        f.engine.compute(*f.id("a"), f.state, nullptr, &cs);

        const int width_id = CssPropertyRegistry::instance().id_of("width");
        const CssValue* first = cs.parsed(width_id);
        CHECK(first != nullptr);
        // The same slot read twice is the same object: that is the whole point.
        CHECK(cs.parsed(width_id) == first);

        CHECK(first->kind() == CssValueKind::Length);
        CHECK(static_cast<const CssLength&>(*first).value == 10);

        // Rewriting the slot re-parses. Asserted on the VALUE, not on pointer
        // identity: the old parse is freed, and the allocator is free to hand
        // the same address back — an inequality check here passed for the wrong
        // reason and then failed for the wrong reason too.
        cs.set(width_id, "20px");
        const CssValue* second = cs.parsed(width_id);
        CHECK(second != nullptr && second->kind() == CssValueKind::Length);
        CHECK(static_cast<const CssLength&>(*second).value == 20);

        // An unset slot falls back to the initial value, and its memo goes with
        // it rather than continuing to describe the removed one.
        cs.unset(width_id);
        const CssValue* third = cs.parsed(width_id);
        CHECK(third == nullptr || third->kind() != CssValueKind::Length ||
              static_cast<const CssLength&>(*third).value != 20);
    }
    // ---- initial values fill everything unset
    {
        Fixture f;
        CHECK(f.html("<div id=a>x</div>"));
        CHECK(f.css("#a { color: red }"));
        CHECK(f.value("a", "display") == "inline");
        CHECK(f.value("a", "position") == "static");
    }
    // ---- inheritance: inherited properties flow down, others do not
    {
        Fixture f;
        CHECK(f.html("<div id=p><span id=c>x</span></div>"));
        CHECK(f.css("#p { color: red; width: 100px }"));
        ComputedStyle parent, child;
        f.engine.compute(*f.id("p"), f.state, nullptr, &parent);
        f.engine.compute(*f.id("c"), f.state, &parent, &child);
        CHECK(parent.get("color") == "red");
        CHECK(child.get("color") == "red");
        CHECK(parent.get("width") == "100px");
        CHECK(child.get("width") == "auto");
    }
    // ---- an explicit child declaration wins over an inherited value
    {
        Fixture f;
        CHECK(f.html("<div id=p><span id=c>x</span></div>"));
        CHECK(f.css("#p { color: red } #c { color: blue }"));
        ComputedStyle parent, child;
        f.engine.compute(*f.id("p"), f.state, nullptr, &parent);
        f.engine.compute(*f.id("c"), f.state, &parent, &child);
        CHECK(child.get("color") == "blue");
    }
    // ---- custom properties inherit, and can come from inline
    {
        Fixture f;
        CHECK(f.html("<div id=p><span id=c style='--local: 2'>x</span></div>"));
        CHECK(f.css("#p { --brand: #f00 }"));
        ComputedStyle parent, child;
        f.engine.compute(*f.id("p"), f.state, nullptr, &parent);
        f.engine.compute(*f.id("c"), f.state, &parent, &child);
        CHECK(parent.get("--brand") == "#f00");
        CHECK(child.get("--brand") == "#f00");
        CHECK(child.get("--local") == "2");
    }
    // ---- combinators and structural pseudo-classes participate
    {
        Fixture f;
        CHECK(f.html("<ul id=l><li id=one>1</li><li id=two>2</li></ul>"));
        CHECK(f.css("ul li { color: red } li:nth-child(2) { color: green }"));
        CHECK(f.value("one", "color") == "red");
        CHECK(f.value("two", "color") == "green");
    }
    // ---- collect_matches returns the trace in cascade order, last wins
    {
        Fixture f;
        CHECK(f.html("<div id=a class=box>x</div>"));
        CHECK(f.css("div { color: red } .box { color: green } #a { color: blue }"));
        auto m = f.engine.collect_matches(*f.id("a"), f.state);
        CHECK(m.size() == 3);
        CHECK(m[0].selector_text == "div");
        CHECK(m[2].selector_text == "#a");
        CHECK(m[2].declaration->value_text == "blue");
    }
}


// CSS Cascade 5 §6.4.4 — cascade layers. `@layer` used to be ignored entirely:
// its rules kept the unlayered ordinal, so they competed on specificity alone
// and a layered rule beat the unlayered one it was written to lose to.
void test_cascade_layers() {
    {
        // The headline rule, and the one menu.html turned on: an UNLAYERED
        // declaration beats a layered one no matter how much more specific the
        // layered selector is.
        Fixture f;
        CHECK(f.html("<button id=b class=btn>x</button>"));
        CHECK(f.css("@layer base { .btn { padding: 4px 8px } }"
                    "button { padding: 8px 20px }"));
        CHECK(f.value("b", "padding-top") == "8px");
        CHECK(f.value("b", "padding-left") == "20px");
    }
    {
        // Between two layers, the LATER one wins — even from the less specific
        // selector, and even though it was declared first here.
        Fixture f;
        CHECK(f.html("<div id=a class=box>x</div>"));
        CHECK(f.css("@layer base, overrides;"
                    "@layer overrides { div { color: green } }"
                    "@layer base { .box { color: red } }"));
        CHECK(f.value("a", "color") == "green");
    }
    {
        // Without the statement form, order is fixed by first mention.
        Fixture f;
        CHECK(f.html("<div id=a class=box>x</div>"));
        CHECK(f.css("@layer base { .box { color: red } }"
                    "@layer overrides { div { color: green } }"));
        CHECK(f.value("a", "color") == "green");
    }
    {
        // Reopening a layer does not move it: `base` stays first even though
        // its second block comes after `overrides`.
        Fixture f;
        CHECK(f.html("<div id=a class=box>x</div>"));
        CHECK(f.css("@layer base { .box { color: red } }"
                    "@layer overrides { div { color: green } }"
                    "@layer base { .box { color: blue } }"));
        CHECK(f.value("a", "color") == "green");
    }
    {
        // A nested layer is named for its parent, so `@layer a { @layer b }`
        // orders as a.b — after a's own unnamed content and before c.
        Fixture f;
        CHECK(f.html("<div id=a class=box>x</div>"));
        CHECK(f.css("@layer outer { @layer inner { .box { color: red } } }"
                    "@layer last { div { color: green } }"));
        CHECK(f.value("a", "color") == "green");
    }
    {
        // !important REVERSES the layer axis: the earlier layer wins, and an
        // unlayered !important loses to a layered one.
        Fixture f;
        CHECK(f.html("<div id=a class=box>x</div>"));
        CHECK(f.css("@layer base, overrides;"
                    "@layer base { .box { color: red !important } }"
                    "@layer overrides { .box { color: green !important } }"
                    "div { color: blue !important }"));
        CHECK(f.value("a", "color") == "red");
    }
    {
        // An anonymous layer is a layer of its own that nothing can reopen, and
        // it still loses to unlayered.
        Fixture f;
        CHECK(f.html("<div id=a class=box>x</div>"));
        CHECK(f.css("@layer { .box { color: red } }"
                    "div { color: green }"));
        CHECK(f.value("a", "color") == "green");
    }
}
