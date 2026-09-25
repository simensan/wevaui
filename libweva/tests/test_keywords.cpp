#include "check.h"
#include "weva/cascade.h"
#include "weva/dom.h"
#include "weva/html.h"
#include "weva/keyword_resolver.h"
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
        Element* e = id(element_id);
        if (!e) return "<no element>";
        ComputedStyle cs;
        engine.compute(*e, state, nullptr, &cs);
        return std::string(cs.get(prop));
    }
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

void test_css_wide_keywords() {
    CHECK(is_css_wide_keyword("inherit") && is_css_wide_keyword("INITIAL"));
    CHECK(is_css_wide_keyword("unset") && is_css_wide_keyword("revert"));
    CHECK(is_css_wide_keyword("revert-layer"));
    CHECK(!is_css_wide_keyword("inherits") && !is_css_wide_keyword("red"));
    CHECK(!is_css_wide_keyword("") && !is_css_wide_keyword("revert-layers"));

    {
        // ---- `inherit` takes the parent's computed value even for a property
        // that does not normally inherit.
        Fixture f;
        CHECK(f.html("<div id=p><div id=c></div></div>"));
        CHECK(f.css("#p { display: flex } #c { display: inherit }"));
        CHECK_EQ(f.value_under("p", "c", "display"), "flex");
        // With no parent there is nothing to inherit, so the initial applies.
        CHECK_EQ(f.value("c", "display"), "inline");
    }
    {
        // ---- `initial` ignores the parent entirely
        Fixture f;
        CHECK(f.html("<div id=p><div id=c></div></div>"));
        CHECK(f.css("#p { color: red } #c { color: initial }"));
        CHECK_EQ(f.value_under("p", "c", "color"), "black");
        // Without the keyword the child would inherit.
        CHECK_EQ(f.value_under("p", "c", "font-size"), "16px");
    }
    {
        // ---- `unset` splits on whether the property inherits: `inherit` for
        // color, `initial` for display.
        Fixture f;
        CHECK(f.html("<div id=p><div id=c></div></div>"));
        CHECK(f.css("#p { color: red; display: flex } #c { color: unset; display: unset }"));
        CHECK_EQ(f.value_under("p", "c", "color"), "red");
        CHECK_EQ(f.value_under("p", "c", "display"), "inline");
    }
    {
        // ---- keywords are matched case-insensitively, with surrounding
        // whitespace tolerated
        Fixture f;
        CHECK(f.html("<div id=p><div id=c></div></div>"));
        CHECK(f.css("#p { color: red } #c { color: INHERIT }"));
        CHECK_EQ(f.value_under("p", "c", "color"), "red");
    }
    {
        // ---- resolution runs AFTER substitution, so `inherit` yields the
        // parent's substituted value rather than the literal `var(--x)`.
        Fixture f;
        CHECK(f.html("<div id=p><div id=c></div></div>"));
        CHECK(f.css("#p { --x: 5px; padding-top: var(--x) }"
                    "#c { padding-top: inherit }"));
        CHECK_EQ(f.value_under("p", "c", "padding-top"), "5px");
    }
}

void test_revert() {
    {
        // ---- `revert` rolls back to the highest-priority match at a LOWER
        // origin: the author declaration disappears and the UA rule applies.
        Fixture f;
        CHECK(f.html("<div id=a></div>"));
        CHECK(f.css("#a { color: green }", DeclarationOrigin::UserAgent));
        CHECK(f.css("#a { color: revert }", DeclarationOrigin::Author));
        CHECK_EQ(f.value("a", "color"), "green");
    }
    {
        // ---- with no lower origin there is nothing to roll back to, and the
        // spec collapses `revert` to `initial`.
        Fixture f;
        CHECK(f.html("<div id=p><div id=c></div></div>"));
        CHECK(f.css("#p { color: red } #c { color: revert }"));
        CHECK_EQ(f.value_under("p", "c", "color"), "black");
    }
    {
        // ---- the rolled-back value is resolved in turn, so a UA `inherit`
        // reached through `revert` still inherits.
        Fixture f;
        CHECK(f.html("<div id=p><div id=c></div></div>"));
        CHECK(f.css("#c { display: inherit }", DeclarationOrigin::UserAgent));
        CHECK(f.css("#p { display: flex } #c { display: revert }", DeclarationOrigin::Author));
        CHECK_EQ(f.value_under("p", "c", "display"), "flex");
    }
    {
        // ---- a chain: author reverts to user, whose value reverts to the UA.
        Fixture f;
        CHECK(f.html("<div id=a></div>"));
        CHECK(f.css("#a { color: green }", DeclarationOrigin::UserAgent));
        CHECK(f.css("#a { color: revert }", DeclarationOrigin::User));
        CHECK(f.css("#a { color: revert }", DeclarationOrigin::Author));
        CHECK_EQ(f.value("a", "color"), "green");
    }
    {
        // ---- `revert-layer` degrades to `revert` when the origin has no lower
        // layer. Every rule is currently unlayered (@layer compilation is not
        // ported), so this is the only reachable path end-to-end today.
        Fixture f;
        CHECK(f.html("<div id=a></div>"));
        CHECK(f.css("#a { color: green }", DeclarationOrigin::UserAgent));
        CHECK(f.css("#a { color: revert-layer }", DeclarationOrigin::Author));
        CHECK_EQ(f.value("a", "color"), "green");
    }
}

void test_revert_layer_rollback() {
    // The layer axis is exercised directly: @layer is parsed but not yet
    // compiled into ordinals, so no stylesheet can produce a layered match.
    // Skipping this until it can would leave the two-pass "nearest lower layer,
    // then latest match within it" logic entirely unverified.
    Declaration base{"color", "green", false};
    Declaration mid{"color", "blue", false};
    Declaration top{"color", "revert-layer", false};

    const auto at = [](const Declaration* d, DeclarationOrigin o, int layer, int src) {
        MatchedDeclaration m;
        m.declaration = d;
        m.origin = o;
        m.source_index = src;
        m.layer_ordinal = layer;
        return m;
    };
    std::vector<MatchedDeclaration> expanded = {
        at(&base, DeclarationOrigin::Author, 1, 0),
        at(&mid, DeclarationOrigin::Author, 2, 1),
        at(&top, DeclarationOrigin::Author, 3, 2),
    };
    CascadeKey winner;
    winner.origin = DeclarationOrigin::Author;
    winner.layer_ordinal = 3;
    // The NEAREST lower layer wins, not the lowest.
    CHECK_EQ(pre_resolve_rollback("color", "revert-layer", expanded, winner), "blue");

    // Within that layer, the latest match wins.
    Declaration mid2{"color", "teal", false};
    expanded.insert(expanded.begin() + 2, at(&mid2, DeclarationOrigin::Author, 2, 3));
    CHECK_EQ(pre_resolve_rollback("color", "revert-layer", expanded, winner), "teal");

    // `revert` ignores layers entirely and drops the whole origin — with no
    // lower origin present it returns the keyword for the resolver to map to
    // the initial value.
    CHECK_EQ(pre_resolve_rollback("color", "revert", expanded, winner), "revert");

    // A lower ORIGIN is what `revert` looks for, at any layer.
    Declaration ua{"color", "gray", false};
    expanded.push_back(at(&ua, DeclarationOrigin::UserAgent, kUnlayeredOrdinal, 4));
    CHECK_EQ(pre_resolve_rollback("color", "revert", expanded, winner), "gray");

    // A value that is not a rollback keyword passes straight through.
    CHECK_EQ(pre_resolve_rollback("color", "red", expanded, winner), "red");
}

void test_custom_property_keywords() {
    {
        // ---- the initial value of a custom property is the empty string, so
        // `initial` clears it rather than falling back to anything.
        Fixture f;
        CHECK(f.html("<div id=p><div id=c></div></div>"));
        CHECK(f.css("#p { --x: 5px; --y: 1px; --z: 2px }"
                    "#c { --x: initial; --y: inherit; --z: unset }"));
        CHECK_EQ(f.value_under("p", "c", "--x"), "");
        // `inherit` and `unset` both take the parent's value: every custom
        // property inherits.
        CHECK_EQ(f.value_under("p", "c", "--y"), "1px");
        CHECK_EQ(f.value_under("p", "c", "--z"), "2px");
    }
    {
        // ---- a resolved custom property feeds var() with the resolved text
        Fixture f;
        CHECK(f.html("<div id=p><div id=c></div></div>"));
        CHECK(f.css("#p { --gap: 7px } #c { --gap: inherit; padding-top: var(--gap) }"));
        CHECK_EQ(f.value_under("p", "c", "padding-top"), "7px");
    }
    {
        // ---- `initial` on a TYPED custom property does not yield the
        // descriptor's initial-value directly: it yields the empty string,
        // which then fails the syntax check and lands on the descriptor's
        // value. The reference takes the same two-step route.
        Fixture f;
        CHECK(f.html("<div id=p><div id=c></div></div>"));
        CHECK(f.css("@property --sz { syntax: '<length>'; initial-value: 2px; inherits: true; }"
                    "#p { --sz: 40px } #c { --sz: initial }"));
        CHECK_EQ(f.value_under("p", "c", "--sz"), "2px");
    }
    {
        // ---- under the universal syntax the empty string is valid, so
        // `initial` really does clear it.
        Fixture f;
        CHECK(f.html("<div id=p><div id=c></div></div>"));
        CHECK(f.css("@property --s { syntax: '*'; initial-value: hi; inherits: true; }"
                    "#p { --s: yo } #c { --s: initial }"));
        CHECK_EQ(f.value_under("p", "c", "--s"), "");
    }
}
