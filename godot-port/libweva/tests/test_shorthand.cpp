#include "check.h"
#include "weva/shorthand.h"
#include <string>
#include <vector>

using namespace weva;

namespace {

// "prop=value;prop=value" for the expansion of `name: value`, or "<not a
// shorthand>" / "<none>" for the two ways nothing is emitted.
std::string expand(std::string_view name, std::string_view value) {
    std::vector<ShorthandLonghand> out;
    if (!expand_shorthand(name, value, &out)) return "<not a shorthand>";
    if (out.empty()) return "<none>";
    std::string s;
    for (const ShorthandLonghand& lh : out) {
        if (!s.empty()) s += ';';
        s += std::string(lh.property) + "=" + lh.value;
    }
    return s;
}

std::string tokens(std::string_view v) {
    std::string s;
    for (std::string_view t : tokenize_shorthand(v)) {
        if (!s.empty()) s += '|';
        s += std::string(t);
    }
    return s;
}

} // namespace

void test_shorthand_tokenizer() {
    CHECK_EQ(tokens("1px 2px"), "1px|2px");
    CHECK_EQ(tokens("  1px   2px  "), "1px|2px");
    // A parenthesised group is one token however much whitespace it contains.
    CHECK_EQ(tokens("calc(1px + 2px) 3px"), "calc(1px + 2px)|3px");
    CHECK_EQ(tokens("rgb(1, 2, 3) solid"), "rgb(1, 2, 3)|solid");
    // Comma and slash are tokens of their own, so a shorthand can find its
    // group separators without re-scanning.
    CHECK_EQ(tokens("1px,2px"), "1px|,|2px");
    CHECK_EQ(tokens("1px / 2px"), "1px|/|2px");
    // A quoted string is one token including its quotes, and a slash or comma
    // inside it is not a separator.
    CHECK_EQ(tokens("\"a b\" c"), "\"a b\"|c");
    CHECK_EQ(tokens("'a/b'"), "'a/b'");
    CHECK_EQ(tokens("url(a/b.png)"), "url(a/b.png)");
    CHECK_EQ(tokens(""), "");
}

void test_shorthand_edges() {
    CHECK_EQ(expand("margin", "5px"),
             "margin-top=5px;margin-right=5px;margin-bottom=5px;margin-left=5px");
    CHECK_EQ(expand("margin", "1px 2px"),
             "margin-top=1px;margin-right=2px;margin-bottom=1px;margin-left=2px");
    // Three values leave `left` mirroring `right`.
    CHECK_EQ(expand("margin", "1px 2px 3px"),
             "margin-top=1px;margin-right=2px;margin-bottom=3px;margin-left=2px");
    CHECK_EQ(expand("margin", "1px 2px 3px 4px"),
             "margin-top=1px;margin-right=2px;margin-bottom=3px;margin-left=4px");

    // `auto` is valid for margin but not for padding, which is the only
    // difference between the two.
    CHECK(expand("margin", "0 auto") != "<none>");
    CHECK_EQ(expand("padding", "0 auto"), "<none>");

    // `inset` fills the BARE side names, not `inset-*`.
    CHECK_EQ(expand("inset", "0"), "top=0;right=0;bottom=0;left=0");

    // Percentages, calc and the other math functions all survive validation.
    CHECK(expand("padding", "10%") != "<none>");
    CHECK(expand("padding", "calc(1px + 2px)") != "<none>");
    CHECK(expand("padding", "clamp(1px, 2vw, 3px)") != "<none>");

    // Five values, zero values, and a non-length all invalidate the whole
    // declaration — not just the offending side.
    CHECK_EQ(expand("margin", "1px 2px 3px 4px 5px"), "<none>");
    CHECK_EQ(expand("margin", ""), "<none>");
    CHECK_EQ(expand("padding", "1px red"), "<none>");
    // A bare number is not a length, except for the literal 0.
    CHECK_EQ(expand("padding", "5"), "<none>");
    CHECK(expand("padding", "0") != "<none>");

    // The <length> validator predates the newer units, so a token it does not
    // recognise takes the whole shorthand down. Reference behaviour, pinned:
    // `padding: 1lh` produces no padding at all.
    CHECK_EQ(expand("padding", "1lh"), "<none>");
    CHECK_EQ(expand("padding", "1cqw"), "<none>");
    CHECK(expand("padding", "1rem") != "<none>");

    CHECK_EQ(expand("margin-inline", "1px 2px"),
             "margin-inline-start=1px;margin-inline-end=2px");
    CHECK_EQ(expand("padding-block", "3px"),
             "padding-block-start=3px;padding-block-end=3px");
    CHECK_EQ(expand("padding-block", "auto"), "<none>");
    CHECK(expand("inset-inline", "auto") != "<none>");
}

void test_shorthand_border() {
    // Width, style and colour in any order; each omitted component resets to
    // its INITIAL value rather than being left alone.
    CHECK_EQ(expand("border-top", "solid"),
             "border-top-width=medium;border-top-style=solid;border-top-color=currentcolor");
    CHECK_EQ(expand("border-top", "2px solid red"),
             "border-top-width=2px;border-top-style=solid;border-top-color=red");
    CHECK_EQ(expand("border-top", "red solid 2px"),
             "border-top-width=2px;border-top-style=solid;border-top-color=red");
    CHECK_EQ(expand("border-top", "#abc dashed"),
             "border-top-width=medium;border-top-style=dashed;border-top-color=#abc");

    // `border` writes all twelve longhands.
    std::vector<ShorthandLonghand> out;
    CHECK(expand_shorthand("border", "1px solid black", &out));
    CHECK(out.size() == 12);

    // A repeated category, or a token in no category, invalidates the value.
    CHECK_EQ(expand("border-top", "solid dashed"), "<none>");
    CHECK_EQ(expand("border-top", "solid nonsense"), "<none>");
    CHECK_EQ(expand("border-top", "1px solid red blue"), "<none>");

    // The four-sided forms use the ordinary 1-to-4 fill.
    CHECK_EQ(expand("border-width", "1px 2px"),
             "border-top-width=1px;border-right-width=2px;"
             "border-bottom-width=1px;border-left-width=2px");
    CHECK_EQ(expand("border-style", "solid"),
             "border-top-style=solid;border-right-style=solid;"
             "border-bottom-style=solid;border-left-style=solid");
    CHECK(expand("border-color", "red blue") != "<none>");
    // Each form validates against its own category.
    CHECK_EQ(expand("border-style", "1px"), "<none>");
    CHECK_EQ(expand("border-width", "solid"), "<none>");
    CHECK_EQ(expand("border-color", "solid"), "<none>");
}

void test_shorthand_radius_and_axes() {
    // Corner fill order is TL, TR, BR, BL — NOT the top/right/bottom/left of
    // the edge shorthands.
    CHECK_EQ(expand("border-radius", "1px 2px"),
             "border-top-left-radius=1px;border-top-right-radius=2px;"
             "border-bottom-right-radius=1px;border-bottom-left-radius=2px");
    // A `/` splits horizontal from vertical radii, giving elliptical corners
    // emitted as two tokens; a circular corner collapses back to one.
    CHECK_EQ(expand("border-radius", "10px / 20px"),
             "border-top-left-radius=10px 20px;border-top-right-radius=10px 20px;"
             "border-bottom-right-radius=10px 20px;border-bottom-left-radius=10px 20px");
    CHECK_EQ(expand("border-radius", "10px / 10px"),
             "border-top-left-radius=10px;border-top-right-radius=10px;"
             "border-bottom-right-radius=10px;border-bottom-left-radius=10px");
    CHECK_EQ(expand("border-radius", "1px 2px 3px 4px 5px"), "<none>");
    CHECK_EQ(expand("border-radius", "10px /"), "<none>");

    CHECK_EQ(expand("gap", "4px"), "row-gap=4px;column-gap=4px");
    CHECK_EQ(expand("gap", "4px 8px"), "row-gap=4px;column-gap=8px");
    CHECK(expand("gap", "normal") != "<none>");
    CHECK_EQ(expand("gap", "auto"), "<none>");

    CHECK_EQ(expand("overflow", "hidden"), "overflow-x=hidden;overflow-y=hidden");
    CHECK_EQ(expand("overflow", "hidden auto"), "overflow-x=hidden;overflow-y=auto");
    CHECK_EQ(expand("overflow", "bogus"), "<none>");
    CHECK_EQ(expand("overscroll-behavior", "contain"),
             "overscroll-behavior-x=contain;overscroll-behavior-y=contain");

    // place-* is ALIGN first then JUSTIFY — the reverse of the x-then-y order
    // the other two-value shorthands use.
    CHECK_EQ(expand("place-items", "center start"),
             "align-items=center;justify-items=start");
    CHECK_EQ(expand("place-content", "center"),
             "align-content=center;justify-content=center");
    CHECK_EQ(expand("place-self", "end"), "align-self=end;justify-self=end");

    // outline mirrors the border triplet but its initial colour is `invert`.
    CHECK_EQ(expand("outline", "solid"),
             "outline-width=medium;outline-style=solid;outline-color=invert");
    CHECK_EQ(expand("outline", "2px dotted invert"),
             "outline-width=2px;outline-style=dotted;outline-color=invert");
    CHECK(expand("outline", "thick solid red") != "<none>");

    // Longhands and unknown properties are not shorthands at all, which is a
    // different answer from "expanded to nothing".
    CHECK_EQ(expand("color", "red"), "<not a shorthand>");
    CHECK_EQ(expand("margin-top", "1px"), "<not a shorthand>");
    CHECK(is_shorthand("margin") && is_shorthand("border") && is_shorthand("place-self"));
    CHECK(!is_shorthand("margin-top") && !is_shorthand("") && !is_shorthand("display"));
}

void test_substitution_defers_expansion() {
    // CSS Values L4 §6.2/§6.3: a value containing var() or attr() cannot be
    // tokenised yet, so expansion is skipped and the declaration stays a
    // shorthand. The reference never revisits it, which is why the layout side
    // still has a raw-shorthand path at all.
    CHECK(contains_substitution("var(--x)"));
    CHECK(contains_substitution("1px var(--x) 3px"));
    CHECK(contains_substitution("attr(data-p)"));
    CHECK(contains_substitution("VAR(--x)"));
    // The '(' guard must not make a paren-free value look like a reference,
    // nor a similarly-named function.
    CHECK(!contains_substitution("1px 2px"));
    CHECK(!contains_substitution("calc(1px + 2px)"));
    CHECK(!contains_substitution(""));
    CHECK(!contains_substitution("variant"));
}
