#include "check.h"
#include "weva/at_property.h"
#include "weva/cascade.h"
#include "weva/dom.h"
#include "weva/html.h"
#include <memory>
#include <optional>
#include <string>

using namespace weva;

namespace {

std::optional<PropertyDescriptor> desc(std::string_view name, const char* syntax,
                                       const char* initial, const char* inherits) {
    return PropertyDescriptor::try_create(
        name,
        syntax ? std::optional<std::string>(syntax) : std::nullopt,
        initial ? std::optional<std::string>(initial) : std::nullopt,
        inherits ? std::optional<std::string>(inherits) : std::nullopt);
}

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

void test_at_property_descriptor() {
    // ---- all three descriptors are required
    CHECK(desc("--a", "<length>", "0px", "false").has_value());
    CHECK(!desc("--a", nullptr, "0px", "false").has_value());
    CHECK(!desc("--a", "<length>", nullptr, "false").has_value());
    CHECK(!desc("--a", "<length>", "0px", nullptr).has_value());
    // A name that is not a custom property is not a property at all.
    CHECK(!desc("color", "<length>", "0px", "false").has_value());
    CHECK(!desc("-a", "<length>", "0px", "false").has_value());

    // ---- `inherits` takes exactly true/false, case-insensitively
    CHECK(desc("--a", "*", "x", "TRUE")->inherits);
    CHECK(!desc("--a", "*", "x", " false ")->inherits);
    CHECK(!desc("--a", "*", "x", "yes").has_value());
    CHECK(!desc("--a", "*", "x", "1").has_value());

    // ---- quotes around the syntax are optional
    CHECK_EQ(desc("--a", "\"<length>\"", "0px", "false")->syntax, "<length>");
    CHECK_EQ(desc("--a", "<length>", "0px", "false")->syntax, "<length>");
    // `syntax: ""` is non-empty as written but empty once unquoted.
    CHECK(!desc("--a", "\"\"", "0px", "false").has_value());

    // ---- present-but-empty initial-value is valid under the universal syntax,
    // which is exactly why a missing descriptor cannot be modelled as "".
    CHECK(desc("--a", "*", "", "false").has_value());
    CHECK_EQ(desc("--a", "*", "", "false")->initial_value, "");

    // ---- §3.4: an initial-value may not reference a variable
    CHECK(!desc("--a", "*", "var(--b)", "false").has_value());
    CHECK(!desc("--a", "*", "calc(1px + env(safe-area-inset-top))", "false").has_value());

    // ---- the initial value must satisfy the declared syntax
    CHECK(desc("--a", "<length>", "10px", "false").has_value());
    CHECK(!desc("--a", "<length>", "red", "false").has_value());
    CHECK(desc("--a", "<color>", "red", "false").has_value());
    CHECK(desc("--a", "<length> | <percentage>", "50%", "false").has_value());
}

void test_at_property_validate() {
    const auto ok = [](const char* syn, const char* v) {
        return PropertyDescriptor::validate(syn, v);
    };
    // ---- lengths: bare zero, every unit, and the math/substitution functions
    CHECK(ok("<length>", "0"));
    CHECK(ok("<length>", "12px") && ok("<length>", "1.5REM") && ok("<length>", "-3em"));
    CHECK(ok("<length>", "50%") && ok("<length>", "+2px"));
    CHECK(ok("<length>", "calc(1px + 2px)") && ok("<length>", "clamp(1px,2px,3px)"));
    CHECK(!ok("<length>", "12") && !ok("<length>", "red") && !ok("<length>", ""));
    // "10min" ends with "in", but "10m" is not a number — and no later unit
    // matches either, so it is not a length.
    CHECK(!ok("<length>", "10min"));

    // ---- numbers vs integers: int.TryParse takes neither a decimal point nor
    // an exponent, where double.TryParse takes both.
    CHECK(ok("<number>", "1.5") && ok("<number>", "-2") && ok("<number>", "1e3"));
    CHECK(ok("<integer>", "42") && ok("<integer>", "-7") && ok("<integer>", "+7"));
    CHECK(!ok("<integer>", "1.5") && !ok("<integer>", "1e3") && !ok("<integer>", "x"));

    CHECK(ok("<percentage>", "50%") && !ok("<percentage>", "50"));
    CHECK(ok("<angle>", "90deg") && ok("<angle>", "0.25turn") && !ok("<angle>", "90"));
    // "ms" is tested before "s", or every millisecond reads as a bad seconds
    // value.
    CHECK(ok("<time>", "200ms") && ok("<time>", "1.5s") && !ok("<time>", "200"));
    CHECK(ok("<resolution>", "2dppx") && ok("<resolution>", "96dpi") && ok("<resolution>", "2x"));

    // ---- colours. The reference checks only the LENGTH of a hex colour, so
    // `#zzzz` validates; reproduced rather than tightened.
    CHECK(ok("<color>", "#fff") && ok("<color>", "#ff0000ff") && ok("<color>", "#zzzz"));
    CHECK(!ok("<color>", "#ff") && !ok("<color>", "#fffff"));
    CHECK(ok("<color>", "rebeccapurple") && ok("<color>", "REBECCAPURPLE"));
    CHECK(ok("<color>", "transparent") && ok("<color>", "currentColor"));
    CHECK(ok("<color>", "oklch(0.5 0.1 200)") && !ok("<color>", "12px"));

    CHECK(ok("<url>", "url(a.png)") && ok("<image>", "linear-gradient(red, blue)"));
    CHECK(ok("<string>", "\"hi\"") && !ok("<string>", "hi"));
    CHECK(ok("<custom-ident>", "foo-bar") && !ok("<custom-ident>", "1abc"));

    // ---- alternatives, multipliers, and the universal syntax
    CHECK(ok("<length> | <color>", "red") && ok("<length> | <color>", "1px"));
    CHECK(!ok("<length> | <color>", "\"s\""));
    CHECK(ok("<length>+", "1px") && ok("<length>#", "1px"));
    CHECK(ok("*", "anything at all") && ok("*", ""));
    // An unmodelled component accepts anything rather than invalidating the
    // author's rule.
    CHECK(ok("<not-a-real-type>", "whatever"));
}

void test_at_property_in_cascade() {
    {
        // ---- inherits: false. The child must NOT see the parent's value; it
        // takes the descriptor's initial value instead.
        Fixture f;
        CHECK(f.html("<div id=p><div id=c></div></div>"));
        CHECK(f.css("@property --shadow { syntax: '*'; initial-value: none; inherits: false; }"
                    "@property --ink { syntax: '<color>'; initial-value: black; inherits: true; }"
                    "#p { --shadow: 4px; --ink: red }"));
        CHECK_EQ(f.value_under("p", "c", "--shadow"), "none");
        CHECK_EQ(f.value_under("p", "c", "--ink"), "red");
        // The declaring element keeps its own authored value either way.
        CHECK_EQ(f.value("p", "--shadow"), "4px");
    }
    {
        // ---- an UNREGISTERED custom property still inherits unconditionally
        Fixture f;
        CHECK(f.html("<div id=p><div id=c></div></div>"));
        CHECK(f.css("#p { --gap: 8px }"));
        CHECK_EQ(f.value_under("p", "c", "--gap"), "8px");
    }
    {
        // ---- initial-value seeding: a registered property is readable on an
        // element no rule touches, at the root as well as below it.
        Fixture f;
        CHECK(f.html("<div id=p><div id=c></div></div>"));
        CHECK(f.css("@property --pad { syntax: '<length>'; initial-value: 6px; inherits: true; }"
                    "@property --sz { syntax: '<length>'; initial-value: 2px; inherits: false; }"));
        CHECK_EQ(f.value("p", "--pad"), "6px");
        CHECK_EQ(f.value("p", "--sz"), "2px");
        CHECK_EQ(f.value_under("p", "c", "--pad"), "6px");
        CHECK_EQ(f.value_under("p", "c", "--sz"), "2px");
    }
    {
        // ---- syntax violation is invalid at computed-value time: the
        // descriptor's initial value applies, not the authored text.
        Fixture f;
        CHECK(f.html("<div id=a></div>"));
        CHECK(f.css("@property --len { syntax: '<length>'; initial-value: 1px; inherits: false; }"
                    "#a { --len: red }"));
        CHECK_EQ(f.value("a", "--len"), "1px");
    }
    {
        Fixture f;
        CHECK(f.html("<div id=a></div>"));
        CHECK(f.css("@property --len { syntax: '<length>'; initial-value: 1px; inherits: false; }"
                    "#a { --len: 9px }"));
        CHECK_EQ(f.value("a", "--len"), "9px");
    }
    {
        // ---- Cascade L5 §7.3: `unset` on a NON-inheriting property is
        // `initial`, not `inherit`. Every custom property looks inherited to a
        // keyword resolver, so only the registry can tell these apart.
        Fixture f;
        CHECK(f.html("<div id=p><div id=c></div></div>"));
        CHECK(f.css("@property --sz { syntax: '<length>'; initial-value: 2px; inherits: false; }"
                    "#p { --sz: 40px } #c { --sz: unset }"));
        CHECK_EQ(f.value_under("p", "c", "--sz"), "2px");
    }
    {
        // ---- a registered property feeds var() like any other custom property
        Fixture f;
        CHECK(f.html("<div id=a></div>"));
        CHECK(f.css("@property --pad { syntax: '<length>'; initial-value: 6px; inherits: true; }"
                    "#a { padding-top: var(--pad) }"));
        CHECK_EQ(f.value("a", "padding-top"), "6px");
    }
    {
        // ---- an invalid @property rule registers nothing, so the property
        // stays an ordinary inheriting custom property.
        Fixture f;
        CHECK(f.html("<div id=p><div id=c></div></div>"));
        CHECK(f.css("@property --x { syntax: '<length>'; initial-value: 1px; inherits: maybe; }"
                    "@property --y { syntax: '<length>'; inherits: false; }"
                    "#p { --x: 5px; --y: 5px }"));
        CHECK(f.engine.property_registry().count() == 0);
        CHECK_EQ(f.value_under("p", "c", "--x"), "5px");
        CHECK_EQ(f.value_under("p", "c", "--y"), "5px");
    }
    {
        // ---- later registration of the same name wins
        Fixture f;
        CHECK(f.html("<div id=a></div>"));
        CHECK(f.css("@property --v { syntax: '<length>'; initial-value: 1px; inherits: false; }"
                    "@property --v { syntax: '<length>'; initial-value: 8px; inherits: false; }"));
        CHECK(f.engine.property_registry().count() == 1);
        CHECK_EQ(f.value("a", "--v"), "8px");
    }
}
