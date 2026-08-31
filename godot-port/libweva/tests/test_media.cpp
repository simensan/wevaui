#include "check.h"
#include "weva/cascade.h"
#include "weva/dom.h"
#include "weva/html.h"
#include "weva/media.h"
#include <memory>
#include <string>

using namespace weva;

void test_media_queries() {
    MediaContext ctx;   // 1920x1080, 96dpi, light, hover, fine, screen

    // ---- media types
    CHECK(evaluate_media_query("all", ctx));
    CHECK(evaluate_media_query("screen", ctx));
    CHECK(!evaluate_media_query("print", ctx));
    CHECK(!evaluate_media_query("tv", ctx));      // unknown type does not match
    CHECK(evaluate_media_query("", ctx));         // bare @media applies

    // ---- width/height ranges
    CHECK(evaluate_media_query("(min-width: 600px)", ctx));
    CHECK(!evaluate_media_query("(min-width: 3000px)", ctx));
    CHECK(evaluate_media_query("(max-width: 3000px)", ctx));
    CHECK(!evaluate_media_query("(max-width: 600px)", ctx));
    CHECK(evaluate_media_query("(min-height: 1080px)", ctx));   // inclusive
    CHECK(evaluate_media_query("(max-height: 1080px)", ctx));

    // ---- relative units resolve against the viewport
    CHECK(evaluate_media_query("(min-width: 50em)", ctx));      // 800px
    CHECK(!evaluate_media_query("(min-width: 200em)", ctx));

    // ---- orientation, with the square-is-landscape rule
    CHECK(evaluate_media_query("(orientation: landscape)", ctx));
    CHECK(!evaluate_media_query("(orientation: portrait)", ctx));
    {
        MediaContext tall = ctx;
        tall.viewport_width_px = 800;
        tall.viewport_height_px = 1200;
        CHECK(evaluate_media_query("(orientation: portrait)", tall));
        MediaContext square = ctx;
        square.viewport_width_px = square.viewport_height_px = 1000;
        CHECK(evaluate_media_query("(orientation: landscape)", square));
    }

    // ---- aspect-ratio
    CHECK(evaluate_media_query("(min-aspect-ratio: 1/1)", ctx));
    CHECK(!evaluate_media_query("(min-aspect-ratio: 2/1)", ctx));

    // ---- preference features
    {
        MediaContext dark = ctx;
        dark.color_scheme = ColorScheme::Dark;
        CHECK(evaluate_media_query("(prefers-color-scheme: dark)", dark));
        CHECK(!evaluate_media_query("(prefers-color-scheme: light)", dark));
        CHECK(evaluate_media_query("(prefers-color-scheme: light)", ctx));

        MediaContext reduced = ctx;
        reduced.prefers_reduced_motion = true;
        CHECK(evaluate_media_query("(prefers-reduced-motion: reduce)", reduced));
        CHECK(!evaluate_media_query("(prefers-reduced-motion: reduce)", ctx));
        CHECK(evaluate_media_query("(prefers-reduced-motion: no-preference)", ctx));
    }

    // ---- interaction capability
    {
        MediaContext touch = ctx;
        touch.hover = HoverCapability::None;
        touch.pointer = PointerCapability::Coarse;
        CHECK(evaluate_media_query("(hover: none)", touch));
        CHECK(evaluate_media_query("(pointer: coarse)", touch));
        CHECK(!evaluate_media_query("(pointer: fine)", touch));
        CHECK(evaluate_media_query("(hover: hover)", ctx));
    }

    // ---- and / or / not / only
    CHECK(evaluate_media_query("screen and (min-width: 600px)", ctx));
    CHECK(!evaluate_media_query("print and (min-width: 600px)", ctx));
    CHECK(!evaluate_media_query("screen and (min-width: 3000px)", ctx));
    CHECK(evaluate_media_query("(min-width: 600px) and (max-width: 3000px)", ctx));
    CHECK(!evaluate_media_query("not screen", ctx));
    CHECK(evaluate_media_query("not print", ctx));
    CHECK(evaluate_media_query("only screen", ctx));
    // a comma list is an OR
    CHECK(evaluate_media_query("print, screen", ctx));
    CHECK(!evaluate_media_query("print, tv", ctx));
    CHECK(evaluate_media_query("(min-width: 3000px), (orientation: landscape)", ctx));

    // ---- an UNKNOWN feature must be false, so its block stays hidden rather
    // than applying unconditionally
    CHECK(!evaluate_media_query("(nonsense-feature: 3)", ctx));
    CHECK(!evaluate_media_query("screen and (nonsense-feature: 3)", ctx));
}

void test_supports() {
    // ---- a known property with a parseable value is supported
    CHECK(evaluate_supports("(display: grid)"));
    CHECK(evaluate_supports("(color: red)"));
    CHECK(evaluate_supports("(width: 10px)"));

    // ---- an unknown property is not
    CHECK(!evaluate_supports("(not-a-property: 1)"));

    // ---- a custom property is always supported per spec
    CHECK(evaluate_supports("(--anything: whatever)"));

    // ---- and / or / not
    CHECK(evaluate_supports("(display: grid) and (color: red)"));
    CHECK(!evaluate_supports("(display: grid) and (not-a-property: 1)"));
    CHECK(evaluate_supports("(not-a-property: 1) or (display: grid)"));
    CHECK(evaluate_supports("not (not-a-property: 1)"));
    CHECK(!evaluate_supports("not (display: grid)"));

    // ---- malformed conditions are unsupported, not silently true
    CHECK(!evaluate_supports(""));
    CHECK(!evaluate_supports("(display)"));
    CHECK(!evaluate_supports("garbage"));
}

void test_conditional_rules_in_cascade() {
    SymbolTable symbols;
    HtmlParseError he;
    ParseOptions o;
    o.strict = false;
    auto doc = parse_html("<div id=a>x</div>", &symbols, o, &he);
    CHECK(static_cast<bool>(doc));

    Stylesheet sheet;
    CssParseError ce;
    CHECK(parse_stylesheet(
        "#a { color: red }"
        "@media (min-width: 3000px) { #a { color: green } }"     // must NOT apply
        "@media (min-width: 600px) { #a { background-color: blue } }"
        "@supports (not-a-property: 1) { #a { color: yellow } }"  // must NOT apply
        "@supports (display: grid) { #a { border-color: teal } }",
        false, &sheet, &ce));

    CascadeEngine eng;
    eng.set_media_context(MediaContext{});   // 1920x1080
    eng.add_stylesheet(&sheet, DeclarationOrigin::Author);
    NullStateProvider st;

    ComputedStyle cs;
    eng.compute(*doc->get_element_by_id("a"), st, nullptr, &cs);
    CHECK(cs.get("color") == "red");              // the 3000px block was skipped
    CHECK(cs.get("background-color") == "blue");  // the 600px block applied
    // `border-color` is a shorthand, so the cascade expands it and drops the
    // shorthand itself — the value has to be read off a longhand.
    CHECK(cs.get("border-top-color") == "teal");  // supported @supports applied

    // ---- a narrow viewport flips which block applies
    Stylesheet sheet2;
    CHECK(parse_stylesheet(
        "#a { color: red }"
        "@media (max-width: 600px) { #a { color: green } }",
        false, &sheet2, &ce));
    CascadeEngine narrow;
    MediaContext small;
    small.viewport_width_px = 480;
    small.viewport_height_px = 800;
    narrow.set_media_context(small);
    narrow.add_stylesheet(&sheet2, DeclarationOrigin::Author);
    ComputedStyle cs2;
    narrow.compute(*doc->get_element_by_id("a"), st, nullptr, &cs2);
    CHECK(cs2.get("color") == "green");
}
