#include "check.h"
#include "weva/css_rule.h"
#include <string>

using namespace weva;

namespace {

struct P {
    Stylesheet sheet;
    CssParseError err;
    bool run(std::string_view src, bool strict = true) {
        err = CssParseError{};
        return parse_stylesheet(src, strict, &sheet, &err);
    }
    // Bounds-checked: a parse that yields no rules must fail the CHECK, not
    // index off the end of the vector.
    StyleRule* style(std::size_t i) {
        if (i >= sheet.rules.size() || sheet.rules[i]->kind() != RuleKind::Style) return nullptr;
        return static_cast<StyleRule*>(sheet.rules[i].get());
    }
    GenericAtRule* at(std::size_t i) {
        if (i >= sheet.rules.size() || sheet.rules[i]->kind() != RuleKind::At) return nullptr;
        return static_cast<GenericAtRule*>(sheet.rules[i].get());
    }
};

} // namespace

void test_css_parser() {
    // ---- selectors, declarations, lowercasing, trimming
    {
        P p;
        CHECK(p.run(".a, .b > c { Color : red ; margin:0 auto }"));
        CHECK(p.sheet.rules.size() == 1);
        auto* r = p.style(0);
        CHECK(r != nullptr);
        CHECK(r && r->selectors.size() == 2);
        CHECK(r->selectors[0] == ".a");
        CHECK(r->selectors[1] == ".b > c");
        CHECK(r->declarations.size() == 2);
        CHECK(r->declarations[0].property == "color");   // property lowercased
        CHECK(r->declarations[0].value_text == "red");   // value trimmed, case kept
        CHECK(r->declarations[1].property == "margin");
        CHECK(r->declarations[1].value_text == "0 auto");
    }

    // ---- value text is REBUILT from tokens, not sliced from source
    {
        P p;
        CHECK(p.run("a{background:url(x.png) no-repeat;content:\"q\";width:calc(1px + 2%)}"));
        auto* r = p.style(0);
        CHECK(r != nullptr);
        if (!r) return;
        CHECK(r->declarations[0].value_text == "url(x.png) no-repeat");
        CHECK(r->declarations[1].value_text == "\"q\"");
        CHECK(r->declarations[2].value_text == "calc(1px + 2%)");
    }

    // ---- !important, including the last-bang rule
    {
        P p;
        CHECK(p.run("a{color:red !important;margin:0!IMPORTANT;top:1px !x !important}"));
        auto* r = p.style(0);
        CHECK(r != nullptr);
        if (!r) return;
        CHECK(r->declarations[0].important && r->declarations[0].value_text == "red");
        CHECK(r->declarations[1].important && r->declarations[1].value_text == "0");
        // C# keeps overwriting `candidate` rather than breaking, so the LAST
        // top-level '!' wins and "!x" stays in the value.
        CHECK(r->declarations[2].important);
        CHECK(r->declarations[2].value_text == "1px !x");
    }

    // ---- a '!' inside a string or parens is not top level
    {
        P p;
        CHECK(p.run("a{content:\"!important\";width:calc(1px)}"));
        auto* r = p.style(0);
        CHECK(r != nullptr);
        if (!r) return;
        CHECK(!r->declarations[0].important);
        CHECK(!r->declarations[1].important);
    }

    // ---- an empty value is rejected, and it takes the NEXT declaration with it.
    //
    // TryParseDeclaration consumes the terminating ';' BEFORE it checks
    // sawNonWs, so on failure the caller's SkipDeclaration starts after that
    // semicolon and runs to the next one — swallowing `margin:0`. Chrome keeps
    // `margin:0` here, so this is a real C#/Chrome divergence. Reproduced
    // rather than fixed, per the standing rule; flagged in PORT_PLAN.md.
    {
        P p;
        CHECK(p.run("a{color:;margin:0}", /*strict=*/false));
        auto* r = p.style(0);
        CHECK(r != nullptr);
        CHECK(r->declarations.empty());
    }

    // ---- an empty value at the END of a block loses only itself
    {
        P p;
        CHECK(p.run("a{margin:0;color:}", /*strict=*/false));
        auto* r = p.style(0);
        CHECK(r != nullptr);
        CHECK(r->declarations.size() == 1);
        CHECK(r->declarations[0].property == "margin");
    }

    // ---- at-rules: statement form and block form
    {
        P p;
        CHECK(p.run("@charset \"utf-8\"; @media (min-width:600px){ a{color:red} }"));
        CHECK(p.sheet.rules.size() == 2);
        auto* c = p.at(0);
        CHECK(c && c->name == "charset" && !c->has_block);
        CHECK(c->prelude == "\"utf-8\"");
        auto* m = p.at(1);
        CHECK(m && m->name == "media" && m->has_block);
        CHECK(m->prelude == "(min-width:600px)");
        CHECK(m->nested_rules.size() == 1);
        auto* inner = static_cast<StyleRule*>(m->nested_rules[0].get());
        CHECK(inner->selectors[0] == "a");
        CHECK(inner->declarations[0].property == "color");
    }

    // ---- at-rule names lowercase; prelude keeps its case
    {
        P p;
        CHECK(p.run("@MEDIA Screen { a{b:c} }"));
        CHECK(p.at(0)->name == "media");
        CHECK(p.at(0)->prelude == "Screen");
    }

    // ---- CSS nesting: declarations and nested rules coexist
    {
        P p;
        CHECK(p.run("a{color:red; &:hover{color:blue} .child{top:0} @media x{b{c:d}}}"));
        auto* r = p.style(0);
        CHECK(r != nullptr);
        if (!r) return;
        CHECK(r->declarations.size() == 1);
        CHECK(r->nested_rules.size() == 3);
        CHECK(static_cast<StyleRule*>(r->nested_rules[0].get())->selectors[0] == "&:hover");
        CHECK(static_cast<StyleRule*>(r->nested_rules[1].get())->selectors[0] == ".child");
        CHECK(r->nested_rules[2]->kind() == RuleKind::At);
    }

    // ---- the ident-colon ambiguity: `color:red` is a declaration,
    // `a:hover{}` is a nested rule. Resolved by scanning to `{` vs `;`/`}`.
    {
        P p;
        CHECK(p.run("x{ color:red; a:hover{top:0} }"));
        auto* r = p.style(0);
        CHECK(r != nullptr);
        if (!r) return;
        CHECK(r->declarations.size() == 1 && r->declarations[0].property == "color");
        CHECK(r->nested_rules.size() == 1);
        CHECK(static_cast<StyleRule*>(r->nested_rules[0].get())->selectors[0] == "a:hover");
    }

    // ---- `::` is always a nested selector
    {
        P p;
        CHECK(p.run("x{ a::before{top:0} }"));
        CHECK(p.style(0)->nested_rules.size() == 1);
    }

    // ---- lenient recovery: a bad rule must not eat the rest of the sheet
    {
        P p;
        CHECK(p.run("a{color:@@@;} b{top:1px} }} c{left:2px}", /*strict=*/false));
        bool saw_b = false, saw_c = false;
        for (auto& r : p.sheet.rules) {
            if (r->kind() != RuleKind::Style) continue;
            auto* s = static_cast<StyleRule*>(r.get());
            for (auto& sel : s->selectors) {
                if (sel == "b") saw_b = true;
                if (sel == "c") saw_c = true;
            }
        }
        CHECK(saw_b);
        CHECK(saw_c);
    }

    // ---- deep nesting is capped rather than overflowing the stack
    {
        std::string hostile;
        for (int i = 0; i < 2000; ++i) hostile += "@media all{";
        hostile += "a{color:red}";
        for (int i = 0; i < 2000; ++i) hostile += "}";
        P p;
        CHECK(p.run(hostile, /*strict=*/false));   // must terminate, not crash
    }

    // ---- unterminated block: strict reports, lenient survives
    {
        P p;
        CHECK(!p.run("a{color:red"));
        CHECK(p.err.message == "Unterminated declaration block");

        P p2;
        CHECK(p2.run("a{color:red", /*strict=*/false));
        CHECK(p2.style(0)->declarations[0].value_text == "red");
    }

    // ---- inline declarations (a style="" body)
    {
        std::vector<Declaration> d;
        CssParseError e;
        CHECK(parse_inline_declarations("color:red; MARGIN : 0 auto ", true, &d, &e));
        CHECK(d.size() == 2);
        CHECK(d[0].property == "color" && d[0].value_text == "red");
        CHECK(d[1].property == "margin" && d[1].value_text == "0 auto");
    }

    // ---- empty and whitespace-only sheets
    {
        P p;
        CHECK(p.run(""));
        CHECK(p.sheet.rules.empty());
        P p2;
        CHECK(p2.run("   \n/* c */  "));
        CHECK(p2.sheet.rules.empty());
    }
}
