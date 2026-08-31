#include "check.h"
#include "weva/cascade.h"
#include "weva/computed_style.h"
#include "weva/dom.h"
#include "weva/html.h"
#include "weva/variable_resolver.h"
#include <memory>
#include <string>

using namespace weva;

namespace {
std::string res(std::string_view v, const ComputedStyle& s, bool* ok) {
    std::string out;
    *ok = resolve_variables(v, s, &out);
    return out;
}
} // namespace

void test_variables() {
    ComputedStyle s;
    s.set("--ink", "#123456");
    s.set("--size", "12px");
    s.set("--nested", "var(--ink)");
    s.set("--empty", "");

    bool ok = false;

    // ---- plain substitution
    CHECK(res("var(--ink)", s, &ok) == "#123456" && ok);
    CHECK(res("1px solid var(--ink)", s, &ok) == "1px solid #123456" && ok);
    CHECK(res("var(--size) var(--size)", s, &ok) == "12px 12px" && ok);

    // ---- values with no var() pass through untouched
    CHECK(res("red", s, &ok) == "red" && ok);

    // ---- case-insensitive function name
    CHECK(res("VAR(--ink)", s, &ok) == "#123456" && ok);

    // ---- nested references resolve transitively
    CHECK(res("var(--nested)", s, &ok) == "#123456" && ok);

    // ---- fallbacks
    CHECK(res("var(--missing, blue)", s, &ok) == "blue" && ok);
    CHECK(res("var(--missing, var(--ink))", s, &ok) == "#123456" && ok);
    // a fallback may itself contain commas
    CHECK(res("var(--missing, 1px, 2px)", s, &ok) == "1px, 2px" && ok);
    // a defined variable ignores its fallback
    CHECK(res("var(--ink, red)", s, &ok) == "#123456" && ok);

    // ---- §3: an unresolvable var() with NO fallback makes the whole
    // declaration invalid at computed-value time. It must NOT degrade to an
    // empty substitution — the caller has to drop the declaration entirely.
    res("var(--missing)", s, &ok);
    CHECK(!ok);
    res("1px solid var(--missing)", s, &ok);
    CHECK(!ok);
    // one bad var() taints the value even when another resolves
    res("var(--ink) var(--missing)", s, &ok);
    CHECK(!ok);

    // ---- an empty custom property is treated as unset, so the fallback wins
    CHECK(res("var(--empty, fallback)", s, &ok) == "fallback" && ok);

    // ---- a malformed first argument is invalid; fallback still honoured
    res("var(notaname)", s, &ok);
    CHECK(!ok);
    CHECK(res("var(notaname, ok)", s, &ok) == "ok" && ok);

    // ---- §3.1 cycles: every property in the cycle is invalid
    {
        ComputedStyle c;
        c.set("--a", "var(--b)");
        c.set("--b", "var(--a)");
        res("var(--a)", c, &ok);
        CHECK(!ok);
        // self-reference is a cycle of one
        ComputedStyle d;
        d.set("--x", "var(--x)");
        res("var(--x)", d, &ok);
        CHECK(!ok);
    }

    // ---- §3.1: a fallback must NOT rescue a cycle member. This is the
    // subtle one — the fallback would otherwise resolve through the still-open
    // stack frame and paper over the cycle.
    {
        ComputedStyle c;
        c.set("--a", "var(--b)");
        c.set("--b", "var(--a, safe)");
        res("var(--a)", c, &ok);
        CHECK(!ok);
    }

    // ---- unbalanced parens emit the remainder verbatim rather than looping
    CHECK(res("var(--ink", s, &ok) == "var(--ink" && ok);

    // ---- deep nesting terminates instead of recursing without bound
    {
        ComputedStyle c;
        for (int i = 0; i < 100; ++i) {
            c.set("--v" + std::to_string(i), "var(--v" + std::to_string(i + 1) + ")");
        }
        c.set("--v100", "done");
        res("var(--v0)", c, &ok);   // exceeds the depth cap; must return, not hang
        CHECK(!ok);
    }
}

void test_variables_in_cascade() {
    SymbolTable symbols;
    HtmlParseError he;
    ParseOptions o;
    o.strict = false;
    auto doc = parse_html(
        "<div id=p><span id=c>x</span><span id=d style='color: var(--nope)'>y</span></div>",
        &symbols, o, &he);
    CHECK(static_cast<bool>(doc));

    Stylesheet sheet;
    CssParseError ce;
    CHECK(parse_stylesheet(
        "#p { --ink: #f00; color: var(--ink) }"
        "#c { border-color: var(--ink) }",
        false, &sheet, &ce));

    CascadeEngine eng;
    eng.add_stylesheet(&sheet, DeclarationOrigin::Author);
    NullStateProvider st;

    ComputedStyle parent, child, bad;
    eng.compute(*doc->get_element_by_id("p"), st, nullptr, &parent);
    CHECK(parent.get("color") == "#f00");           // resolved, not literal
    CHECK(parent.get("--ink") == "#f00");

    // ---- a var() referencing an ANCESTOR's custom property resolves
    eng.compute(*doc->get_element_by_id("c"), st, &parent, &child);
    CHECK(child.get("--ink") == "#f00");            // inherited
    CHECK(child.get("border-color") == "#f00");

    // ---- an unresolvable var() drops the declaration, so the property falls
    // back to inherited/initial rather than keeping the literal text.
    eng.compute(*doc->get_element_by_id("d"), st, &parent, &bad);
    CHECK(bad.get("color") != "var(--nope)");
    CHECK(bad.get("color") == "#f00");              // inherited from #p
}
