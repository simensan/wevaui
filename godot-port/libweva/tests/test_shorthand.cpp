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

void test_shorthand_font() {
    // CSS Fonts L4 §4.4: the shorthand resets every longhand it covers, so
    // `font: bold 14px sans-serif` gives a 14px size AND a normal line-height.
    CHECK_EQ(expand("font", "bold 14px sans-serif"),
             "font-style=normal;font-variant=normal;font-weight=bold;font-stretch=normal;"
             "font-size=14px;line-height=normal;font-family=sans-serif");
    CHECK_EQ(expand("font", "italic small-caps 700 12px/1.5 \"Segoe UI\", Arial, sans-serif"),
             "font-style=italic;font-variant=small-caps;font-weight=700;font-stretch=normal;"
             "font-size=12px;line-height=1.5;font-family=\"Segoe UI\", Arial, sans-serif");
    // The size and the family are mandatory; a system font keyword is not
    // resolved and drops the declaration rather than half-applying it.
    CHECK_EQ(expand("font", "bold sans-serif"), "<none>");
    CHECK_EQ(expand("font", "14px"), "<none>");
    CHECK_EQ(expand("font", "menu"), "<none>");
    CHECK_EQ(expand("font", "inherit"),
             "font-style=inherit;font-variant=inherit;font-weight=inherit;font-stretch=inherit;"
             "font-size=inherit;line-height=inherit;font-family=inherit");
    CHECK(is_shorthand("font"));
}

// CSS Multi-column L1 3.3 `columns`. Multi-column layout reads column-count
// and column-width and works; the shorthand was never expanded, so `columns: 3`
// -- the form almost everybody writes -- laid out as a single column while
// `column-count: 3` laid out as three.
void test_shorthand_columns() {
    // An integer is the count, a length is the width, and the free slot takes
    // `auto`. Getting this backwards is the whole risk in the property.
    CHECK_EQ(expand("columns", "3"), "column-width=auto;column-count=3");
    CHECK_EQ(expand("columns", "12em"), "column-width=12em;column-count=auto");
    CHECK_EQ(expand("columns", "200px"), "column-width=200px;column-count=auto");

    // Both, in either order.
    CHECK_EQ(expand("columns", "3 12em"), "column-width=12em;column-count=3");
    CHECK_EQ(expand("columns", "12em 3"), "column-width=12em;column-count=3");

    // `auto` fills whichever slot is still free.
    CHECK_EQ(expand("columns", "auto"), "column-width=auto;column-count=auto");
    CHECK_EQ(expand("columns", "auto 3"), "column-width=auto;column-count=3");
    CHECK_EQ(expand("columns", "3 auto"), "column-width=auto;column-count=3");
    CHECK_EQ(expand("columns", "auto 12em"), "column-width=12em;column-count=auto");

    // A bare number with a unit must not be read as a count: `12em` is a
    // width, and the digit prefix is exactly what a looser check gets wrong.
    CHECK_EQ(expand("columns", "12em 4em"), "<none>");
    CHECK_EQ(expand("columns", "3 4"), "<none>");
    CHECK_EQ(expand("columns", "50%"), "<none>");
    CHECK_EQ(expand("columns", "3.5"), "<none>");
    CHECK_EQ(expand("columns", "wide"), "<none>");
    CHECK_EQ(expand("columns", "3 12em auto"), "<none>");

    CHECK(is_shorthand("columns"));
}

// CSS Flexbox L1 5.1 `flex-flow`. Unexpanded, `flex-flow: column wrap` set
// NEITHER longhand -- a column layout came out a row, which is not a subtle
// kind of wrong.
void test_shorthand_flex_flow() {
    // Both, in either order.
    CHECK_EQ(expand("flex-flow", "column wrap"), "flex-direction=column;flex-wrap=wrap");
    CHECK_EQ(expand("flex-flow", "wrap column"), "flex-direction=column;flex-wrap=wrap");

    // Either alone, with the other taking its initial value -- which is the
    // half a naive two-token split gets wrong.
    CHECK_EQ(expand("flex-flow", "column"), "flex-direction=column;flex-wrap=nowrap");
    CHECK_EQ(expand("flex-flow", "wrap"), "flex-direction=row;flex-wrap=wrap");
    CHECK_EQ(expand("flex-flow", "row-reverse"), "flex-direction=row-reverse;flex-wrap=nowrap");
    CHECK_EQ(expand("flex-flow", "wrap-reverse"), "flex-direction=row;flex-wrap=wrap-reverse");

    // A token belonging to neither drops the whole declaration rather than
    // setting half of it.
    CHECK_EQ(expand("flex-flow", "column 10px"), "<none>");
    CHECK_EQ(expand("flex-flow", "sideways"), "<none>");
    // And two of the same kind is not a valid pair either.
    CHECK_EQ(expand("flex-flow", "row column"), "<none>");
}

// CSS Text Decoration L4 2.5. `text-decoration: underline dotted red` is the
// natural way to write one, and without this expansion only the LINE arrived:
// every rule came out solid and in the text's own colour.
void test_shorthand_text_decoration() {
    // The three parts, in any order.
    CHECK_EQ(expand("text-decoration", "underline dotted red"),
             "text-decoration-line=underline;text-decoration-style=dotted;"
             "text-decoration-color=red");
    CHECK_EQ(expand("text-decoration", "red dotted underline"),
             "text-decoration-line=underline;text-decoration-style=dotted;"
             "text-decoration-color=red");

    // The parts left out take their initial values, which is what makes a
    // bare `text-decoration: underline` mean solid and currentcolor.
    CHECK_EQ(expand("text-decoration", "underline"),
             "text-decoration-line=underline;text-decoration-style=solid;"
             "text-decoration-color=currentcolor");

    // The line part is itself a SET: two lines at once is one declaration.
    CHECK_EQ(expand("text-decoration", "underline overline"),
             "text-decoration-line=underline overline;text-decoration-style=solid;"
             "text-decoration-color=currentcolor");

    // `none` cannot join that set -- it is the absence of one -- so it is
    // valid alone and invalid beside another line.
    CHECK_EQ(expand("text-decoration", "none"),
             "text-decoration-line=none;text-decoration-style=solid;"
             "text-decoration-color=currentcolor");
    CHECK_EQ(expand("text-decoration", "none underline"), "<none>");
    CHECK_EQ(expand("text-decoration", "underline none"), "<none>");

    // A token belonging to none of the three drops the declaration rather
    // than setting part of it.
    CHECK_EQ(expand("text-decoration", "underline 4px"), "<none>");
    // And a second of the same kind is not a valid pair.
    CHECK_EQ(expand("text-decoration", "dotted dashed"), "<none>");
}
