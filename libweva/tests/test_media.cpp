#include "check.h"
#include "weva/cascade.h"
#include "weva/dom.h"
#include "weva/html.h"
#include "weva/media.h"
#include "weva/container_query.h"
#include <memory>
#include <string>

using namespace weva;

void test_container_size_queries() {
    ContainerSizeContext c;
    c.width=300; c.height=150;
    c.lengths.base_font_size_px=20;
    c.lengths.root_font_size_px=16;
    c.lengths.viewport_width_px=1000;
    const auto check = [&](std::string_view text, QueryTruth expected, uint8_t axes) {
        ContainerSizeQuery query;
        CHECK(query.parse(text));
        CHECK(query.evaluate(c)==expected);
        CHECK(query.required_axes(false)==axes);
    };
    check("(min-width: 300px)",QueryTruth::True,1);
    check("(width > 300px)",QueryTruth::False,1);
    check("(300px <= width)",QueryTruth::True,1);
    check("(200px <    width <= 300px)",QueryTruth::True,1);
    check("(400px >= width > 300px)",QueryTruth::False,1);
    check("(width: 15em)",QueryTruth::True,1);
    check("(width: 18.75rem)",QueryTruth::True,1);
    check("(width: 30vw)",QueryTruth::True,1);
    check("(width: calc(10em + 100px))",QueryTruth::True,1);
    check("(height: 150px)",QueryTruth::True,2);
    check("(orientation: landscape)",QueryTruth::True,3);
    check("(aspect-ratio: 2/1)",QueryTruth::True,3);
    check("(1/1 < aspect-ratio <= 2/1)",QueryTruth::True,3);
    check("(width)",QueryTruth::True,1);
    check("not (width > 400px)",QueryTruth::True,1);
    check("(width: 300px) and (height: 150px)",QueryTruth::True,3);
    check("((width: 300px) or (height: 0px)) and (width)",QueryTruth::True,3);
    check("not (unrecognized: yes)",QueryTruth::Unknown,0);
    check("(unrecognized: yes) or (width: 300px)",QueryTruth::True,1);
    check("(unrecognized: yes) and (width: 0px)",QueryTruth::False,1);
    check("not style(--theme: dark)",QueryTruth::Unknown,0);
    check("not (width: 50%)",QueryTruth::Unknown,1);
    check("not (orientation: banana)",QueryTruth::Unknown,3);
    check("(width: 300)",QueryTruth::Unknown,1);
    check("(width: calc(300))",QueryTruth::Unknown,1);
    check("(width: calc(20px / 0))",QueryTruth::Unknown,1);
    c.lengths.has_basis=true; c.lengths.basis_pixels=2800;
    check("(width: calc(20px + 10%))",QueryTruth::Unknown,1);
    for (auto bad : {"", "(width", "width: 20px", "(width) and", "(width) or (height) and (width)",
        "not (width) and (height)", "(0px < width > 20px)", "(0px < width < 300px < 400px)"}) {
        ContainerSizeQuery query;
        CHECK(!query.parse(bad));
        CHECK(query.evaluate(c)==QueryTruth::Unknown);
    }
    c.width=c.height=150;
    check("(orientation: portrait)",QueryTruth::True,3);
    c.vertical=true;
    c.height=300;
    ContainerSizeQuery logical;
    CHECK(logical.parse("(inline-size: 300px)"));
    CHECK(logical.required_axes(true)==2);
    CHECK(logical.evaluate(c)==QueryTruth::True);
    CHECK(logical.parse("(block-size: 150px)"));
    CHECK(logical.required_axes(true)==1);
    CHECK(logical.evaluate(c)==QueryTruth::True);
}

void test_container_conditional_cascade() {
    SymbolTable symbols;
    ParseOptions options; options.strict=false;
    HtmlParseError he;
    auto doc=parse_html("<section id=parent><i>A</i><i>B</i></section>",&symbols,options,&he);
    CHECK(static_cast<bool>(doc));
    auto* parent=doc->get_element_by_id("parent");
    const auto& a=static_cast<const Element&>(*parent->children()[0]);
    const auto& b=static_cast<const Element&>(*parent->children()[1]);
    Stylesheet sheet;
    CssParseError ce;
    CHECK(parse_stylesheet("i{color:red} @container Card (width >= 300px){"
        "i{color:green} @container (height > 100px){i{background-color:blue}}"
        "i::before{content:'wide'}} @container NOT (width){i{color:yellow}}",false,&sheet,&ce));
    CascadeEngine engine;
    engine.add_stylesheet(&sheet,DeclarationOrigin::Author);
    CHECK(engine.container_queries().size()==3);
    CHECK(engine.container_queries()[0].name=="Card");
    CHECK(engine.container_queries()[1].name.empty());
    CHECK(engine.container_queries()[2].name.empty());
    NullStateProvider state;
    ComputedStyle style, pseudo;
    engine.compute(a,state,nullptr,&style);
    CHECK(style.get("color")=="red"); // No layout context yet.
    struct Inputs : ContainerQueryProvider {
        const CascadeEngine* engine=nullptr;
        const Element* first=nullptr;
        double width=300;
        bool matches(const Element& e,size_t query) const override {
            ContainerSizeContext c;
            c.width=&e==first ? width : 200; c.height=150;
            return engine->container_queries()[query].condition.evaluate(c)==QueryTruth::True;
        }
        uint64_t version(const Element& e) const override {
            return &e==first ? static_cast<uint64_t>(width) : 200;
        }
    } inputs;
    inputs.engine=&engine; inputs.first=&a;
    engine.set_container_provider(&inputs);
    engine.compute(a,state,nullptr,&style);
    CHECK(style.get("color")=="green");
    CHECK(style.get("background-color")=="blue");
    CHECK(engine.compute_pseudo_element(a,"before",state,style,&pseudo));
    engine.compute(b,state,nullptr,&style);
    CHECK(style.get("color")=="red"); // Identical DOM shapes, different input versions.
    CHECK(style.get("background-color")!="blue");
    CHECK(!engine.compute_pseudo_element(b,"before",state,style,&pseudo));
    inputs.width=250;
    engine.compute(a,state,nullptr,&style);
    CHECK(style.get("color")=="red"); // Changed inputs, no global cache invalidation.
    inputs.width=300;
    engine.compute(a,state,nullptr,&style);
    CHECK(style.get("color")=="green");
    const auto hits=engine.cache_stats().hits;
    engine.compute(a,state,nullptr,&style);
    CHECK(engine.cache_stats().hits==hits+1);
    engine.clear();
    CHECK(engine.container_queries().empty());
}

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
    CHECK(!evaluate_supports("(display: made-up)"));
    CHECK(!evaluate_supports("(display: 42px)"));
    CHECK(evaluate_supports("(display: INLINE)"));
    CHECK(evaluate_supports("(display: revert-layer)"));
    CHECK(evaluate_supports("(display: var(--mode))"));
    CHECK(!evaluate_supports("(display: var(mode))"));
    CHECK(!evaluate_supports("(display: var())"));
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
