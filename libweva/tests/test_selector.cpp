#include "check.h"
#include "weva/selector.h"
#include <string>

using namespace weva;

namespace {
struct S {
    CompiledSelector sel;
    SelectorParseError err;
    bool run(std::string_view t) {
        sel = CompiledSelector{};
        err = SelectorParseError{};
        return parse_selector(t, &sel, &err);
    }
    Specificity spec() { return sel.specificity(); }
    const CompoundSelector& compound(std::size_t i) { return sel.sequence.compounds[i]; }
};
bool spec_is(Specificity s, int a, int b, int c) { return s.a == a && s.b == b && s.c == c; }
} // namespace

void test_selector() {
    // ---- specificity, the thing the cascade actually consumes
    {
        S s;
        CHECK(s.run("*") && spec_is(s.spec(), 0, 0, 0));
        CHECK(s.run("div") && spec_is(s.spec(), 0, 0, 1));
        CHECK(s.run(".cls") && spec_is(s.spec(), 0, 1, 0));
        CHECK(s.run("#id") && spec_is(s.spec(), 1, 0, 0));
        CHECK(s.run("[attr]") && spec_is(s.spec(), 0, 1, 0));
        CHECK(s.run(":hover") && spec_is(s.spec(), 0, 1, 0));
        CHECK(s.run("::before") && spec_is(s.spec(), 0, 0, 1));
        CHECK(s.run("#a.b.c div") && spec_is(s.spec(), 1, 2, 1));
    }

    // ---- :where contributes ZERO; :is/:not/:has take their max
    {
        S s;
        CHECK(s.run(":where(#id, div)") && spec_is(s.spec(), 0, 0, 0));
        CHECK(s.run(":is(#id, div)") && spec_is(s.spec(), 1, 0, 0));
        CHECK(s.run(":not(.a, #b)") && spec_is(s.spec(), 1, 0, 0));
        CHECK(s.run(":has(> .child)") && spec_is(s.spec(), 0, 1, 0));
        // A :where() wrapper still lets its own compound count.
        CHECK(s.run("div:where(.anything)") && spec_is(s.spec(), 0, 0, 1));
    }

    // ---- :nth-child(An+B of S) is (0,1,0) plus the max of S
    {
        S s;
        CHECK(s.run(":nth-child(2n+1)") && spec_is(s.spec(), 0, 1, 0));
        CHECK(s.run(":nth-child(2n+1 of #x)") && spec_is(s.spec(), 1, 1, 0));
    }

    // ---- combinators
    {
        S s;
        CHECK(s.run("a b"));
        CHECK(s.sel.sequence.combinators.size() == 1);
        CHECK(s.sel.sequence.combinators[0] == Combinator::Descendant);
        CHECK(s.run("a > b") && s.sel.sequence.combinators[0] == Combinator::Child);
        CHECK(s.run("a + b") && s.sel.sequence.combinators[0] == Combinator::AdjacentSibling);
        CHECK(s.run("a ~ b") && s.sel.sequence.combinators[0] == Combinator::GeneralSibling);
        // whitespace around a combinator is optional
        CHECK(s.run("a>b") && s.sel.sequence.combinators[0] == Combinator::Child);
        CHECK(s.run("a > b c") && s.sel.sequence.compounds.size() == 3);
        // dangling and doubled combinators are errors
        CHECK(!s.run("a >"));
        CHECK(!s.run("a > > b"));
        CHECK(!s.run("> a"));
    }

    // ---- nth expressions
    {
        S s;
        auto nth = [&](const char* t) -> NthExpression {
            s.run(t);
            const auto& p = s.compound(0).parts[0];
            return static_cast<const PseudoClassSelector&>(*p).nth;
        };
        CHECK(nth(":nth-child(odd)").a == 2 && nth(":nth-child(odd)").b == 1);
        CHECK(nth(":nth-child(even)").a == 2 && nth(":nth-child(even)").b == 0);
        CHECK(nth(":nth-child(3)").a == 0 && nth(":nth-child(3)").b == 3);
        CHECK(nth(":nth-child(2n)").a == 2 && nth(":nth-child(2n)").b == 0);
        CHECK(nth(":nth-child(2n+1)").a == 2 && nth(":nth-child(2n+1)").b == 1);
        CHECK(nth(":nth-child(-n+3)").a == -1 && nth(":nth-child(-n+3)").b == 3);
        CHECK(nth(":nth-child(2n - 1)").b == -1);   // whitespace around the sign

        // and the matching arithmetic
        NthExpression odd{2, 1};
        CHECK(odd.matches(1) && !odd.matches(2) && odd.matches(3));
        NthExpression firstThree{-1, 3};
        CHECK(firstThree.matches(1) && firstThree.matches(3) && !firstThree.matches(4));
    }

    // ---- attribute selectors, all operators
    {
        S s;
        auto attr = [&](const char* t) -> const AttributeSelector& {
            s.run(t);
            return static_cast<const AttributeSelector&>(*s.compound(0).parts[0]);
        };
        CHECK(attr("[a]").op == AttributeOperator::Exists);
        CHECK(attr("[a=b]").op == AttributeOperator::Equals);
        CHECK(attr("[a~=b]").op == AttributeOperator::WhitespaceContains);
        CHECK(attr("[a|=b]").op == AttributeOperator::DashMatch);
        CHECK(attr("[a^=b]").op == AttributeOperator::Prefix);
        CHECK(attr("[a$=b]").op == AttributeOperator::Suffix);
        CHECK(attr("[a*=b]").op == AttributeOperator::Substring);
        // dash_prefix is precomputed so matching doesn't concatenate per test
        CHECK(attr("[lang|=en]").dash_prefix == "en-");
        // quoted values, and the case-sensitivity flags
        CHECK(attr("[a=\"x y\"]").value == "x y");
        CHECK(attr("[a='x']").value == "x");
        CHECK(attr("[a=b i]").case_insensitive);
        CHECK(!attr("[a=b s]").case_insensitive);
        CHECK(!attr("[a=b]").case_insensitive);
        // attribute NAMES lowercase; values keep their case
        CHECK(attr("[DATA-X=Foo]").name == "data-x");
        CHECK(attr("[DATA-X=Foo]").value == "Foo");
        CHECK(!s.run("[a"));
        CHECK(!s.run("[a=]"));
    }

    // ---- pseudo-elements, including the legacy single-colon forms
    {
        S s;
        CHECK(s.run("::before") && s.compound(0).pseudo_element == "before");
        CHECK(s.run("::BEFORE") && s.compound(0).pseudo_element == "before");
        CHECK(s.run(":before") && s.compound(0).has_pseudo_element);
        CHECK(s.run("div::after") && s.compound(0).pseudo_element == "after");
        CHECK(!s.run("::nonsense"));
        CHECK(!s.run("::before::after"));
        // nothing may follow a pseudo-element
        CHECK(!s.run("::before.cls"));
    }

    // ---- type selectors lowercase; class and id keep case (HTML is
    // case-insensitive for tags, case-sensitive for ids/classes)
    {
        S s;
        CHECK(s.run("DIV"));
        CHECK(static_cast<const TypeSelector&>(*s.compound(0).parts[0]).tag_name == "div");
        CHECK(s.run(".MyClass"));
        CHECK(static_cast<const ClassSelector&>(*s.compound(0).parts[0]).class_name == "MyClass");
        CHECK(s.run("#MyId"));
        CHECK(static_cast<const IdSelector&>(*s.compound(0).parts[0]).id == "MyId");
    }

    // ---- namespace prefixes are dropped (no @namespace machinery)
    {
        S s;
        CHECK(s.run("svg|circle"));
        CHECK(static_cast<const TypeSelector&>(*s.compound(0).parts[0]).tag_name == "circle");
        CHECK(s.run("*|div"));
        CHECK(s.run("*|*"));
        // ...but |= is still the dash-match operator, not a prefix
        CHECK(s.run("[lang|=en]"));
    }

    // ---- selector lists
    {
        std::vector<CompiledSelector> list;
        SelectorParseError e;
        CHECK(parse_selector_list(".a, .b > c, #d", &list, &e));
        CHECK(list.size() == 3);
        CHECK(spec_is(list[0].specificity(), 0, 1, 0));
        CHECK(spec_is(list[1].specificity(), 0, 1, 1));
        CHECK(spec_is(list[2].specificity(), 1, 0, 0));
    }

    // ---- nested functional selectors
    {
        S s;
        CHECK(s.run(":is(.a, :not(.b)) > :has(.c)"));
        CHECK(s.sel.sequence.compounds.size() == 2);
        CHECK(s.run("a:not(.x):hover"));
        CHECK(s.compound(0).parts.size() == 3);
    }

    // ---- source text is trimmed
    {
        S s;
        CHECK(s.run("  .card:hover > .title  "));
        CHECK(s.sel.source_text == ".card:hover > .title");
    }

    // ---- errors report rather than throw
    {
        S s;
        CHECK(!s.run(""));
        CHECK(!s.run("."));
        CHECK(!s.run("#"));
        CHECK(!s.run(":nonsense"));
        CHECK(!s.err.message.empty());
        CHECK(s.err.column > 0);
    }
}

void test_selector_has() {
    S s;
    // ---- :has() takes a RELATIVE selector list: items may lead with a
    // combinator, which a normal sequence rejects. The leading relation is
    // encoded as a synthetic universal anchor compound at the front.
    CHECK(s.run(":has(> .child)"));
    {
        const auto& p = static_cast<const PseudoClassSelector&>(*s.compound(0).parts[0]);
        CHECK(p.inner_list.size() == 1);
        const CompoundSequence& inner = *p.inner_list[0];
        CHECK(inner.compounds.size() == 2);          // anchor + .child
        CHECK(inner.combinators.size() == 1);
        CHECK(inner.combinators[0] == Combinator::Child);
        CHECK(inner.compounds[0].parts[0]->tag() == SimpleSelector::Tag::Universal);
    }
    CHECK(s.run(":has(+ .sib)"));
    CHECK(s.run(":has(~ .sib)"));
    // A bare descendant relative selector still gets the anchor, with
    // Descendant as the leading relation.
    CHECK(s.run(":has(.desc)"));
    {
        const auto& p = static_cast<const PseudoClassSelector&>(*s.compound(0).parts[0]);
        CHECK(p.inner_list[0]->combinators[0] == Combinator::Descendant);
    }
    CHECK(s.run(":has(> .a, + .b)"));
    CHECK(static_cast<const PseudoClassSelector&>(*s.compound(0).parts[0]).inner_list.size() == 2);

    // ---- :has() may not nest inside :has() — the matcher has no base case
    // for a self-referential subject.
    CHECK(!s.run(":has(:has(.x))"));
    CHECK(s.err.message.find("nested") != std::string::npos);
    // ...but :has inside :is inside :has is still rejected, since the guard
    // spans the whole argument parse.
    CHECK(!s.run(":has(:is(:has(.x)))"));

    // ---- a leading combinator is still an error OUTSIDE a relative context
    CHECK(!s.run("> .child"));
    CHECK(!s.run(":is(> .child)"));
}
