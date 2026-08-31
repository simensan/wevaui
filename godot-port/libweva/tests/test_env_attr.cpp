#include "check.h"
#include "weva/cascade.h"
#include "weva/dom.h"
#include "weva/env_attr.h"
#include "weva/html.h"
#include <memory>
#include <string>

using namespace weva;

void test_env() {
    auto& env = EnvironmentVariables::instance();
    env.reset_to_defaults();
    std::string out;

    // ---- safe-area insets are pre-seeded to 0px, so env(safe-area-inset-top)
    // works on a host with no notch instead of being unresolvable
    CHECK(resolve_env("env(safe-area-inset-top)", &out) && out == "0px");
    CHECK(resolve_env("calc(8px + env(safe-area-inset-left))", &out));
    CHECK(out == "calc(8px + 0px)");

    env.set("safe-area-inset-top", "44px");
    CHECK(resolve_env("env(safe-area-inset-top)", &out) && out == "44px");
    CHECK(resolve_env("10px", &out) && out == "10px");

    // ---- fallbacks
    CHECK(resolve_env("env(nope, 12px)", &out) && out == "12px");
    CHECK(resolve_env("env(nope, env(safe-area-inset-top))", &out) && out == "44px");

    // ---- unresolvable with NO fallback taints the declaration, like var()
    CHECK(!resolve_env("env(nope)", &out));
    CHECK(!resolve_env("1px solid env(nope)", &out));

    // ---- an index list after the name is tolerated; the name is token one
    env.set("viewport-segment-width", "5px");
    CHECK(resolve_env("env(viewport-segment-width 0 0)", &out) && out == "5px");
    CHECK(resolve_env("ENV(safe-area-inset-top)", &out) && out == "44px");

    env.reset_to_defaults();
}

void test_attr() {
    SymbolTable sym;
    HtmlParseError he;
    ParseOptions o;
    o.strict = false;
    auto doc = parse_html(
        "<div id=a data-label='Hello' data-n='42' data-f='2.5' data-c='#ff0000' "
        "data-len='10px' data-bad='xyz'>x</div>", &sym, o, &he);
    CHECK(static_cast<bool>(doc));
    Element* e = doc->get_element_by_id("a");

    // ---- default type is <string>
    CHECK(resolve_attr("attr(data-label)", *e) == "Hello");
    CHECK(resolve_attr("prefix attr(data-label) suffix", *e) == "prefix Hello suffix");

    // ---- typed forms
    CHECK(resolve_attr("attr(data-n number)", *e) == "42");
    CHECK(resolve_attr("attr(data-n px)", *e) == "42px");
    CHECK(resolve_attr("attr(data-f number)", *e) == "2.5");
    CHECK(resolve_attr("attr(data-n %)", *e) == "42%");
    CHECK(resolve_attr("attr(data-n percentage)", *e) == "42%");
    CHECK(resolve_attr("attr(data-n integer)", *e) == "42");
    CHECK(resolve_attr("attr(data-len length)", *e) == "10px");
    CHECK(resolve_attr("attr(data-c color)", *e) == "#ff0000");
    CHECK(resolve_attr("attr(data-label ident)", *e) == "Hello");

    // ---- a value that will not format as the requested type falls back
    CHECK(resolve_attr("attr(data-bad number, 7)", *e) == "7");
    CHECK(resolve_attr("attr(data-f integer, 0)", *e) == "0");   // 2.5 is not an integer

    // ---- a MISSING attribute falls back to the EMPTY STRING, not to
    // invalid-at-computed-value-time. That differs from var() and env(), and
    // matches the C#'s older CSS 2.1 attr() behaviour.
    CHECK(resolve_attr("attr(data-missing)", *e).empty());
    CHECK(resolve_attr("attr(data-missing, fb)", *e) == "fb");

    CHECK(resolve_attr("red", *e) == "red");
    CHECK(resolve_attr("ATTR(data-label)", *e) == "Hello");
}

void test_env_attr_in_cascade() {
    EnvironmentVariables::instance().reset_to_defaults();
    EnvironmentVariables::instance().set("safe-area-inset-top", "20px");

    SymbolTable sym;
    HtmlParseError he;
    ParseOptions o;
    o.strict = false;
    auto doc = parse_html("<div id=a data-w='120'>x</div><div id=b>y</div>", &sym, o, &he);
    CHECK(static_cast<bool>(doc));

    Stylesheet sheet;
    CssParseError ce;
    CHECK(parse_stylesheet(
        "#a { padding-top: env(safe-area-inset-top); width: attr(data-w px) }"
        "#b { margin-top: env(no-such-var) }",
        false, &sheet, &ce));

    CascadeEngine eng;
    eng.add_stylesheet(&sheet, DeclarationOrigin::Author);
    NullStateProvider st;

    ComputedStyle a;
    eng.compute(*doc->get_element_by_id("a"), st, nullptr, &a);
    CHECK(a.get("padding-top") == "20px");
    CHECK(a.get("width") == "120px");

    // ---- an env() with no fallback drops its declaration, so the property
    // falls through to its initial rather than keeping the literal text
    ComputedStyle b;
    eng.compute(*doc->get_element_by_id("b"), st, nullptr, &b);
    CHECK(b.get("margin-top") != "env(no-such-var)");
    CHECK(b.get("margin-top") == "0");

    EnvironmentVariables::instance().reset_to_defaults();
}
