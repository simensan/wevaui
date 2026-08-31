#include "check.h"
#include "weva/cascade.h"
#include "weva/dom.h"
#include "weva/html.h"
#include "weva/style_resolver.h"
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
    std::vector<std::unique_ptr<ComputedStyle>> owned;
    LayoutContext ctx;

    bool css(std::string_view c) {
        auto s = std::make_unique<Stylesheet>();
        CssParseError e;
        if (!parse_stylesheet(c, false, s.get(), &e)) return false;
        engine.add_stylesheet(s.get(), DeclarationOrigin::Author);
        sheets.push_back(std::move(s));
        return true;
    }
    bool html(std::string_view h) {
        HtmlParseError e;
        ParseOptions o;
        o.strict = false;
        doc = parse_html(h, &symbols, o, &e);
        return static_cast<bool>(doc);
    }
    // Computes the style of `id`, chaining through its ancestors so inherited
    // values are present.
    const ComputedStyle* style(std::string_view id) {
        Element* e = doc->get_element_by_id(id);
        if (!e) return nullptr;
        std::vector<Element*> chain;
        for (Node* n = e; n; n = n->parent()) {
            if (n->node_type() == NodeType::Element) chain.push_back(static_cast<Element*>(n));
        }
        const ComputedStyle* parent = nullptr;
        for (size_t i = chain.size(); i-- > 0;) {
            auto cs = std::make_unique<ComputedStyle>();
            engine.compute(*chain[i], state, parent, cs.get());
            parent = cs.get();
            owned.push_back(std::move(cs));
        }
        return parent;
    }
    const ComputedStyle* parent_style(std::string_view id) {
        Element* e = doc->get_element_by_id(id);
        if (!e || !e->parent() || e->parent()->node_type() != NodeType::Element) return nullptr;
        return style(static_cast<Element*>(e->parent())->get_attribute("id"));
    }
};

bool near(double a, double b) { return a - b < 1e-9 && b - a < 1e-9; }

} // namespace

void test_resolve_length() {
    LayoutContext ctx;
    ctx.viewport_width_px = 1000;
    ctx.viewport_height_px = 500;
    ctx.root_font_size_px = 16;

    const auto r = [&](std::string_view raw, std::optional<double> basis = std::nullopt) {
        return resolve_length(raw, ctx, /*font_size=*/20, basis);
    };

    CHECK(r("10px").kind == LengthKind::Length && near(r("10px").pixels, 10));
    CHECK(near(r("2em").pixels, 40));    // em is the ELEMENT's font size
    CHECK(near(r("2rem").pixels, 32));   // rem is the ROOT's
    CHECK(near(r("10vw").pixels, 100));
    CHECK(near(r("10vh").pixels, 50));
    CHECK(near(r("calc(10px + 2em)").pixels, 50));

    // ---- auto / none are distinct kinds, and neither is zero
    CHECK(r("auto").kind == LengthKind::Auto);
    CHECK(r("none").kind == LengthKind::None);
    CHECK(r("").kind == LengthKind::Auto);
    // Intrinsic keywords degrade to auto until intrinsic sizing exists.
    CHECK(r("min-content").kind == LengthKind::Auto);
    CHECK(r("max-content").kind == LengthKind::Auto);
    // A value that does not parse is auto, not zero.
    CHECK(r("bogus").kind == LengthKind::Auto);

    // ---- a percentage with no basis stays a percentage, so the caller keeps
    // its own fallback rather than silently resolving against zero.
    CHECK(r("50%").kind == LengthKind::Percent && near(r("50%").percent, 50));
    CHECK(r("50%", 200.0).kind == LengthKind::Length);
    CHECK(near(r("50%", 200.0).pixels, 100));
    CHECK(near(r("50%", 0.0).pixels, 0));   // a basis of zero is still a basis

    // ---- fit-content(<length-percentage>) surfaces the resolved argument for
    // the caller to clamp; the bare keyword is auto.
    CHECK(r("fit-content").kind == LengthKind::Auto);
    CHECK(r("fit-content(30px)").kind == LengthKind::FitContent);
    CHECK(near(r("fit-content(30px)").pixels, 30));
    CHECK(near(r("fit-content(25%)", 400.0).pixels, 100));
    // A negative argument clamps to zero rather than inverting the box.
    CHECK(near(r("fit-content(-5px)").pixels, 0));

    // ---- resolve_length_px collapses every non-length kind to the fallback
    CHECK(near(resolve_length_px("10px", 99, ctx, 20, std::nullopt), 10));
    CHECK(near(resolve_length_px("auto", 99, ctx, 20, std::nullopt), 99));
    CHECK(near(resolve_length_px("none", 99, ctx, 20, std::nullopt), 99));
    CHECK(near(resolve_length_px("50%", 99, ctx, 20, std::nullopt), 99));
    CHECK(near(resolve_length_px("50%", 99, ctx, 20, 200.0), 100));
}

void test_resolve_border_width() {
    LayoutContext ctx;
    CHECK(near(resolve_border_width("thin", 16, ctx), 1));
    CHECK(near(resolve_border_width("medium", 16, ctx), 3));
    CHECK(near(resolve_border_width("thick", 16, ctx), 5));
    CHECK(near(resolve_border_width("THICK", 16, ctx), 5));
    CHECK(near(resolve_border_width("4px", 16, ctx), 4));
    CHECK(near(resolve_border_width("0.5em", 16, ctx), 8));
    CHECK(near(resolve_border_width("calc(1px + 1px)", 16, ctx), 2));
    // An empty or unparseable border-width is 0, NOT the initial `medium`: a
    // border that failed to parse should not appear.
    CHECK(near(resolve_border_width("", 16, ctx), 0));
    CHECK(near(resolve_border_width("bogus", 16, ctx), 0));
}

void test_font_size_resolution() {
    LayoutContext ctx;
    Fixture f;
    CHECK(f.css("#px { font-size: 24px } #em { font-size: 2em } #pct { font-size: 150% }"
                "#kw { font-size: large } #sm { font-size: smaller }"
                "#calc { font-size: calc(10px + 0.5em) } #none {}"));
    CHECK(f.html("<div id=px></div><div id=em></div><div id=pct></div><div id=kw></div>"
                 "<div id=sm></div><div id=calc></div><div id=none></div>"));

    // With no parent, everything resolves against the root size.
    CHECK(near(font_size_px(f.style("px"), nullptr, ctx), 24));
    CHECK(near(font_size_px(f.style("em"), nullptr, ctx), 32));
    CHECK(near(font_size_px(f.style("pct"), nullptr, ctx), 24));
    CHECK(near(font_size_px(f.style("kw"), nullptr, ctx), 16 * kFontSizeLarge));
    CHECK(near(font_size_px(f.style("sm"), nullptr, ctx), 16 * kFontSizeSmaller));
    // calc's em resolves against the PARENT size, not the element's own.
    CHECK(near(font_size_px(f.style("calc"), nullptr, ctx), 18));
    // An element with no font-size inherits, which with no parent is the root.
    CHECK(near(font_size_px(f.style("none"), nullptr, ctx), 16));
    CHECK(near(font_size_px(nullptr, nullptr, ctx), 16));

    // A different root size moves every relative form.
    LayoutContext big;
    big.root_font_size_px = 20;
    CHECK(near(font_size_px(f.style("em"), nullptr, big), 40));
    CHECK(near(font_size_px(f.style("px"), nullptr, big), 24));
}

void test_font_size_em_chain_limit() {
    // The C# resolves the PARENT's font-size with a null grandparent, i.e.
    // against the root. So `em` compounds for exactly two levels and then
    // stops. Ported as-is for differential parity; this test pins the
    // behaviour rather than endorsing it. Chrome would give .c 128px here.
    LayoutContext ctx;
    Fixture f;
    CHECK(f.css("#a { font-size: 2em } #b { font-size: 2em } #c { font-size: 2em }"));
    CHECK(f.html("<div id=a><div id=b><div id=c></div></div></div>"));

    CHECK(near(font_size_px(f.style("a"), nullptr, ctx), 32));
    CHECK(near(font_size_px(f.style("b"), f.style("a"), ctx), 64));
    // Correct per CSS would be 128: b is really 64, but it re-resolves as 32.
    CHECK(near(font_size_px(f.style("c"), f.style("b"), ctx), 64));
}

void test_line_height_resolution() {
    LayoutContext ctx;
    Fixture f;
    CHECK(f.css("#n { line-height: normal } #num { line-height: 1.5 }"
                "#px { line-height: 30px } #pct { line-height: 200% }"
                "#em { line-height: 2em } #none {}"));
    CHECK(f.html("<div id=n></div><div id=num></div><div id=px></div>"
                 "<div id=pct></div><div id=em></div><div id=none></div>"));

    CHECK(near(line_height_px(f.style("n"), 20, ctx), 24));   // 1.2 x
    CHECK(near(line_height_px(f.style("none"), 20, ctx), 24));
    // A unitless line-height is a MULTIPLIER — the same syntax means pixels
    // for font-size, which is the trap this pins.
    CHECK(near(line_height_px(f.style("num"), 20, ctx), 30));
    CHECK(near(line_height_px(f.style("px"), 20, ctx), 30));
    CHECK(near(line_height_px(f.style("pct"), 20, ctx), 40));
    CHECK(near(line_height_px(f.style("em"), 20, ctx), 40));
}

void test_box_sides() {
    {
        // The shorthand is a FALLBACK: it applies only when all four longhands
        // are at their initial value, because shorthand expansion does not
        // happen at cascade time.
        Fixture f;
        CHECK(f.css("#one { padding: 5px }"
                    "#two { padding: 5px 10px }"
                    "#three { padding: 1px 2px 3px }"
                    "#four { padding: 1px 2px 3px 4px }"
                    "#mixed { padding: 5px; padding-left: 20px }"
                    "#long { padding-top: 7px }"
                    "#calc { padding: calc(1px + 2px) 8px }"));
        CHECK(f.html("<div id=one></div><div id=two></div><div id=three></div>"
                     "<div id=four></div><div id=mixed></div><div id=long></div>"
                     "<div id=calc></div>"));

        BoxSideValues s = box_sides(f.style("one"), "padding");
        CHECK(s.top == "5px" && s.right == "5px" && s.bottom == "5px" && s.left == "5px");
        s = box_sides(f.style("two"), "padding");
        CHECK(s.top == "5px" && s.right == "10px" && s.bottom == "5px" && s.left == "10px");
        s = box_sides(f.style("three"), "padding");
        CHECK(s.top == "1px" && s.right == "2px" && s.bottom == "3px" && s.left == "2px");
        s = box_sides(f.style("four"), "padding");
        CHECK(s.top == "1px" && s.right == "2px" && s.bottom == "3px" && s.left == "4px");

        // One non-initial longhand suppresses the shorthand ENTIRELY — the
        // other three fall back to the initial value, not to the shorthand.
        s = box_sides(f.style("mixed"), "padding");
        CHECK(s.left == "20px" && s.top == "0" && s.right == "0" && s.bottom == "0");
        s = box_sides(f.style("long"), "padding");
        CHECK(s.top == "7px" && s.right == "0");

        // Splitting is depth-aware, so a calc() stays one token.
        s = box_sides(f.style("calc"), "padding");
        CHECK(s.top == "calc(1px + 2px)" && s.right == "8px");
    }
    {
        // An unset property gives "0" on every side, never an empty string.
        Fixture f;
        CHECK(f.html("<div id=a></div>"));
        BoxSideValues s = box_sides(f.style("a"), "margin");
        CHECK(s.top == "0" && s.right == "0" && s.bottom == "0" && s.left == "0");
        s = box_sides(nullptr, "margin");
        CHECK(s.top == "0" && s.left == "0");
    }
}

void test_aspect_ratio_and_direction() {
    Fixture f;
    CHECK(f.css("#r { aspect-ratio: 16 / 9 } #n { aspect-ratio: 1.5 }"
                "#a { aspect-ratio: auto } #both { aspect-ratio: auto 16 / 9 }"
                "#trail { aspect-ratio: 4 / 3 auto } #zero { aspect-ratio: 0 / 5 }"
                "#neg { aspect-ratio: -2 } #rtl { direction: rtl } #ltr {}"));
    CHECK(f.html("<div id=r></div><div id=n></div><div id=a></div><div id=both></div>"
                 "<div id=trail></div><div id=zero></div><div id=neg></div>"
                 "<div id=rtl></div><div id=ltr></div>"));

    double v = 0;
    CHECK(try_resolve_aspect_ratio(f.style("r"), &v) && near(v, 16.0 / 9.0));
    CHECK(try_resolve_aspect_ratio(f.style("n"), &v) && near(v, 1.5));
    CHECK(!try_resolve_aspect_ratio(f.style("a"), &v));
    // With both forms present the explicit ratio wins and the keyword is
    // stripped, from either end.
    CHECK(try_resolve_aspect_ratio(f.style("both"), &v) && near(v, 16.0 / 9.0));
    CHECK(try_resolve_aspect_ratio(f.style("trail"), &v) && near(v, 4.0 / 3.0));
    // A non-positive ratio is not a ratio.
    CHECK(!try_resolve_aspect_ratio(f.style("zero"), &v));
    CHECK(!try_resolve_aspect_ratio(f.style("neg"), &v));
    CHECK(!try_resolve_aspect_ratio(nullptr, &v));

    CHECK(is_rtl(f.style("rtl")));
    CHECK(!is_rtl(f.style("ltr")));
    CHECK(!is_rtl(nullptr));
}
