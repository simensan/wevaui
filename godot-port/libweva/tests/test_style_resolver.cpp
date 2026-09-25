#include "check.h"
#include "weva/block_layout.h"
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

    // Cached syntax must still resolve fresh values for every context, and
    // declaration replacement/unset/clear must not retain an earlier width.
    const auto& registry = CssPropertyRegistry::instance();
    const int ids[] = {registry.id_of("border-top-width"), registry.id_of("border-right-width"),
                      registry.id_of("border-bottom-width"), registry.id_of("border-left-width")};
    ComputedStyle style;
    for (const char* raw : {"thin", "medium", "THICK", "1px", "1.5px", "+.5PX", "-0px",
                            "0.5em", "2rem", "2vw", "3vh", "1pt", "1rlh", "1lh",
                            "calc(1px + 0.5em + 1vw)", "clamp(1px,2vw,8px)",
                            "0", "2", "5%", "bogus", "1.2.3px"}) {
        for (int id : ids) {
            style.set(id, raw);
            const auto version = style.version();
            for (int changed = 0; changed < 6; ++changed) {
                LayoutContext input;
                input.viewport_width_px = changed % 2 ? 500 : 100;
                input.viewport_height_px = changed % 3 ? 300 : 200;
                input.root_font_size_px = 16 + changed;
                input.root_line_height_px = 20 + changed;
                input.dpi_pixels_per_inch = changed % 2 ? 144 : 96;
                const double font_size = 12 + changed * 2;
                const double fresh = resolve_border_width(raw, font_size, input);
                CHECK(near(resolve_border_width(&style, id, font_size, input), fresh));
                CHECK(near(resolve_border_width(&style, id, font_size, input), fresh));
                CHECK(style.version() == version);
            }
            style.unset(id);
            CHECK(near(resolve_border_width(&style, id, 16, ctx), 3));
        }
        style.clear();
        CHECK(near(resolve_border_width(&style, ids[0], 16, ctx), 3));
    }
    CHECK(near(resolve_border_width(nullptr, ids[0], 16, ctx), 0));

    style.set("border-top-style", "solid");
    style.set("border-top-width", "0.5em");
    style.set("border-right-style", "hidden");
    style.set("border-right-width", "99px");
    style.set("border-bottom-style", "solid");
    style.set("border-bottom-width", "2vw");
    style.set("border-left-style", "none");
    style.set("border-left-width", "4px");
    ctx.viewport_width_px = 100;
    auto edges = resolve_border_edges(&style, ctx, 16);
    CHECK(near(edges.top, 8) && near(edges.right, 0) && near(edges.bottom, 2) && near(edges.left, 0));
    ctx.viewport_width_px = 200;
    style.set("border-left-style", "solid");
    edges = resolve_border_edges(&style, ctx, 20);
    CHECK(near(edges.top, 10) && near(edges.right, 0) && near(edges.bottom, 4) && near(edges.left, 4));
    style.set("border-top-width", "3px");
    style.unset(ids[2]);
    edges = resolve_border_edges(&style, ctx, 20);
    CHECK(near(edges.top, 3) && near(edges.bottom, 3));

    // Registry initial values have their own lifetime and can change while a
    // style remains untouched. They must not borrow the owned-value cache key.
    auto& mutable_registry = CssPropertyRegistry::instance();
    const std::string initial(registry.initial_value(ids[0]));
    const bool inherited = registry.is_inherited(ids[0]);
    style.clear();
    mutable_registry.register_property("border-top-width", false, "1px");
    CHECK(near(resolve_border_width(&style, ids[0], 16, ctx), 1));
    mutable_registry.register_property("border-top-width", false, "2px");
    CHECK(near(resolve_border_width(&style, ids[0], 16, ctx), 2));
    style.set(ids[0], "5px");
    CHECK(near(resolve_border_width(&style, ids[0], 16, ctx), 5));
    mutable_registry.register_property("border-top-width", false, "3px");
    CHECK(near(resolve_border_width(&style, ids[0], 16, ctx), 5));
    style.unset(ids[0]);
    CHECK(near(resolve_border_width(&style, ids[0], 16, ctx), 3));
    mutable_registry.register_property("border-top-width", inherited, initial);
    CHECK(near(resolve_border_width(&style, ids[0], 16, ctx), 3));
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

    // Reuse the SAME style across context changes. The earlier cases create
    // a fresh style for every query and therefore cannot detect a stale memo.
    // Keep the parent's explicit pixel size constant so changing root inputs
    // cannot incidentally invalidate the child through a different parent size.
    ComputedStyle fixed_parent;
    fixed_parent.set("font-size", "17px");
    LayoutContext base;
    base.viewport_width_px = 100;
    base.viewport_height_px = 200;
    base.root_line_height_px = 20;
    std::vector<LayoutContext> contexts(8, base);
    contexts[1].viewport_width_px = 400;
    contexts[2].viewport_height_px = 50;
    contexts[3].root_font_size_px = 24;
    contexts[4].root_line_height_px = 30;
    contexts[5].dpi_pixels_per_inch = 192;
    contexts[6] = contexts[1];
    contexts[6].viewport_height_px = 50;
    contexts[6].root_font_size_px = 24;
    contexts[6].root_line_height_px = 30;
    contexts[6].dpi_pixels_per_inch = 192;
    // The last context restores all original inputs after the mixed change.
    const char* forms[] = {"10vw", "10vh", "10vmin", "10vmax", "10svw", "10svh",
        "10lvw", "10lvh", "10dvw", "10dvh", "2rem", "2rlh", "12pt", "1in",
        "calc(1em + 2rem + 1vw + 1vh + 1in + 1rlh)",
        "clamp(8px, 10vw, 30px)", "max(1rem, 10vh)", "14px", "2em", "150%"};
    for (const char* form : forms) {
        ComputedStyle cached;
        cached.set("font-size", form);
        cached.set_inherit_parent(&fixed_parent);
        const auto version = cached.version();
        for (size_t i = 0; i < contexts.size(); ++i) {
            ComputedStyle fresh;
            fresh.set("font-size", form);
            fresh.set_inherit_parent(&fixed_parent);
            const double expected = font_size_px(&fresh, &fixed_parent, contexts[i]);
            const double actual = font_size_px(&cached, &fixed_parent, contexts[i]);
            if (!near(actual, expected))
                std::printf("  font-size context [%s, %zu]: %.9g vs fresh %.9g\n", form, i, actual, expected);
            CHECK(near(actual, expected));
            CHECK(near(font_size_px(&cached, &fixed_parent, contexts[i]), expected));
            CHECK(cached.version() == version);
        }
    }

    // Explicit values independently pin the relevant unit conversions.
    ComputedStyle physical, root_relative, root_line;
    physical.set("font-size", "12pt");
    root_relative.set("font-size", "2rem");
    root_line.set("font-size", "2rlh");
    CHECK(near(font_size_px(&physical, &fixed_parent, base), 16));
    CHECK(near(font_size_px(&physical, &fixed_parent, contexts[5]), 32));
    CHECK(near(font_size_px(&root_relative, &fixed_parent, base), 32));
    CHECK(near(font_size_px(&root_relative, &fixed_parent, contexts[3]), 48));
    CHECK(near(font_size_px(&root_line, &fixed_parent, base), 40));
    CHECK(near(font_size_px(&root_line, &fixed_parent, contexts[4]), 60));

    // Switching away from an owned absolute value must restore all relative
    // dependencies, including after clear/unset and inheritance changes.
    ComputedStyle changing;
    changing.set("font-size", "14px");
    CHECK(near(font_size_px(&changing, &fixed_parent, base), 14));
    fixed_parent.set("font-size", "25px");
    CHECK(near(font_size_px(&changing, &fixed_parent, contexts[6]), 14));
    changing.set("font-size", "10vw");
    CHECK(near(font_size_px(&changing, &fixed_parent, base), 10));
    CHECK(near(font_size_px(&changing, &fixed_parent, contexts[1]), 40));
    changing.clear();
    changing.set("font-size", "12px");
    CHECK(near(font_size_px(&changing, &fixed_parent, base), 12));
    fixed_parent.set("font-size", "10vw");
    changing.set_inherit_parent(&fixed_parent);
    changing.unset(CssPropertyRegistry::instance().id_of("font-size"));
    CHECK(near(font_size_px(&changing, &fixed_parent, base), 10));
    CHECK(near(font_size_px(&changing, &fixed_parent, contexts[1]), 40));
}

void test_font_size_inheritance_chain() {
    // Relative declarations use the parent's computed size at every depth;
    // an undeclared child inherits that size without applying the ratio again.
    LayoutContext ctx;
    Fixture f;
    CHECK(f.css("#a { font-size: 2em } #b { font-size: 2em } #c { font-size: 2em }"));
    CHECK(f.html("<div id=a><div id=b><div id=c><div id=d></div></div></div></div>"));

    CHECK(near(font_size_px(f.style("a"), nullptr, ctx), 32));
    CHECK(near(font_size_px(f.style("b"), f.style("a"), ctx), 64));
    CHECK(near(font_size_px(f.style("c"), f.style("b"), ctx), 128));
    CHECK(near(font_size_px(f.style("d"), f.style("c"), ctx), 128));

    struct Case { const char* a; const char* b; const char* c; double expected; };
    const Case cases[] = {
        {"150%", "150%", "150%", 54},
        {"2em", "150%", "calc(1em + 4px)", 52},
        {"2em", "inherit", "inherit", 32},
        {"2em", "unset", "unset", 32},
        {"2em", "revert", "revert-layer", 32},
        {"2em", "initial", "2em", 32},
        {"2em", "var(--size, inherit)", "inherit !important", 32},
    };
    for (const auto& c : cases) {
        Fixture g;
        CHECK(g.css(std::string("#a{font-size:") + c.a + "}#b{font-size:" + c.b +
                    "}#c{font-size:" + c.c + "}"));
        CHECK(g.html("<div id=a><div id=b><div id=c><div id=d></div></div></div></div>"));
        CHECK(near(font_size_px(g.style("c"), nullptr, ctx), c.expected));
        CHECK(near(font_size_px(g.style("d"), nullptr, ctx), c.expected));
    }
    Fixture layers;
    CHECK(layers.css("#a{font-size:2em}@layer first,second;"
                     "@layer first{#b{font-size:150%}}@layer second{#b{font-size:revert-layer}}"));
    CHECK(layers.html("<div id=a><div id=b></div></div>"));
    CHECK(near(font_size_px(layers.style("b"), nullptr, ctx), 48));

    for (const char* declaration : {"", "font-size:inherit", "font-size:unset",
                                    "font-size:2em", "font-size:initial"}) {
        Fixture pseudo;
        CHECK(pseudo.css(std::string("#a{font-size:2em}#b{font-size:150%}#b::before{content:'x';") +
                         declaration + "}"));
        CHECK(pseudo.html("<div id=a><div id=b></div></div>"));
        const auto host = pseudo.style("b");
        ComputedStyle before;
        CHECK(pseudo.engine.compute_pseudo_element(*pseudo.doc->get_element_by_id("b"),
                                                   "before", pseudo.state, *host, &before));
        const double expected = std::string(declaration) == "font-size:2em" ? 96 :
            std::string(declaration) == "font-size:initial" ? 16 : 48;
        CHECK(near(font_size_px(&before, nullptr, ctx), expected));
    }

    // Provenance is an input version even when raw syntax stays identical.
    const int font_id = CssPropertyRegistry::instance().id_of("font-size");
    ComputedStyle base, parent, child, own;
    base.set("font-size", "16px");
    parent.set_inherit_parent(&base);
    parent.set(font_id, "2em");
    child.set_inherit_parent(&parent);
    child.set(font_id, "2em");
    child.set_important(font_id, true);
    own.set_inherit_parent(&parent);
    own.set(font_id, "2em");
    own.set_important(font_id, true);
    CHECK(near(font_size_px(&child, nullptr, ctx), 64));
    const auto authored_version = child.version();
    child.mark_font_size_inherited();
    CHECK(child.version() != authored_version);
    CHECK(child.is_important(font_id));
    CHECK(near(font_size_px(&child, nullptr, ctx), 32));
    std::vector<int> changed;
    bool unattributed = false;
    CHECK(child.differs_from(own, &changed, &unattributed));
    CHECK(!unattributed && changed.size() == 1 && changed[0] == font_id);
    const auto inherited_version = child.version();
    child.mark_font_size_inherited();
    CHECK(child.version() == inherited_version);
    child.set(font_id, "2em");
    CHECK(child.version() != inherited_version);
    CHECK(!child.font_size_inherited());
    CHECK(near(font_size_px(&child, nullptr, ctx), 64));
    child.unset(font_id);
    CHECK(near(font_size_px(&child, nullptr, ctx), 32));
    CHECK(child.differs_from(own, nullptr, nullptr));

    // Cached descendants track the ancestor's computed size, including
    // repeated updates, reparenting, zero and a deep lazy inheritance chain.
    std::vector<std::unique_ptr<ComputedStyle>> chain;
    const ComputedStyle* previous = &parent;
    for (int i = 0; i < 256; ++i) {
        auto next = std::make_unique<ComputedStyle>();
        next->set_inherit_parent(previous);
        if (i == 100) next->set(font_id, "150%");
        previous = next.get();
        chain.push_back(std::move(next));
    }
    for (int px : {20, 12, 0, 16}) {
        base.set(font_id, std::to_string(px) + "px");
        CHECK(near(font_size_px(previous, nullptr, ctx), px * 3.0));
        CHECK(near(font_size_px(previous, nullptr, ctx), px * 3.0));
    }
    child.set_inherit_parent(&base);
    CHECK(near(font_size_px(&child, &parent, ctx), 16));
    child.set(font_id, "16px");
    child.mark_font_size_inherited();
    ComputedStyle moved(std::move(child));
    CHECK(moved.font_size_inherited());
    CHECK(near(font_size_px(&moved, nullptr, ctx), 16));
    child.clear();
    CHECK(!child.font_size_inherited());
    child.set(font_id, "2em");
    CHECK(near(font_size_px(&child, &parent, ctx), 64));
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

void test_line_height_inheritance() {
    LayoutContext ctx;
    struct Case { const char* raw; double p, c, g; };
    const Case cases[] = {
        {"150%",30,30,30}, {"2em",40,40,40}, {"32px",32,32,32},
        {"calc(1em + 50% + 2px)",32,32,32}, {"2rem",32,32,32},
        {"1.5",30,15,45}, {"calc(1 + 0.5)",30,15,45},
        {"min(1.5,2)",30,15,45}, {"clamp(1.25,1.5,2)",30,15,45},
        {"calc(1em - 30px)",0,0,0}, {"calc(-1)",0,0,0}, {"0",0,0,0},
    };
    for (const char* display : {"block", "contents"})
    for (const auto& sample : cases)
    for (const char* inherit : {"", "inherit", "unset", "revert", "revert-layer", "var(--missing)"}) {
        Fixture f;
        CHECK(f.css(std::string("#p{font-size:20px;line-height:") + sample.raw +
            "}#c{font-size:10px;display:" + display + ";line-height:" + inherit + "}#g{font-size:30px}"));
        CHECK(f.html("<div id=p><div id=c><div id=g>x</div></div></div>"));
        const auto* p = f.style("p"); const auto* c = f.style("c"); const auto* g = f.style("g");
        if (!near(line_height_px(c, 10, ctx), sample.c) || !near(line_height_px(g, 30, ctx), sample.g))
            std::printf("  line-height %s/%s/%s: child %.6f vs %.6f, grandchild %.6f vs %.6f\n",
                display, sample.raw, inherit, line_height_px(c, 10, ctx), sample.c,
                line_height_px(g, 30, ctx), sample.g);
        CHECK(near(line_height_px(p, 20, ctx), sample.p));
        CHECK(near(line_height_px(c, 10, ctx), sample.c));
        CHECK(near(line_height_px(g, 30, ctx), sample.g));
    }
    for (const char* raw : {"", "inherit", "unset", "2em", "1.5", "calc(1 + 0.5)", "initial"}) {
        Fixture f;
        CHECK(f.css(std::string("#p{font-size:20px;line-height:150%}#c{font-size:10px}"
            "#c::before{content:'x';font-size:5px;line-height:") + raw + "}"));
        CHECK(f.html("<div id=p><div id=c></div></div>"));
        ComputedStyle before;
        CHECK(f.engine.compute_pseudo_element(*f.doc->get_element_by_id("c"), "before", f.state,
                                              *f.style("c"), &before));
        const std::string value(raw);
        const double expected = value == "initial" ? 6 : value == "2em" ? 10 :
            value == "1.5" || value == "calc(1 + 0.5)" ? 7.5 : 30;
        CHECK(near(line_height_px(&before, 5, ctx), expected));
    }
    {
        Fixture f;
        CHECK(f.css("#p{font-size:20px;line-height:150%}#c{font-size:10px}"
            "@layer first,second;@layer first{#c{line-height:2em}}@layer second{#c{line-height:revert-layer}}"));
        CHECK(f.html("<div id=p><div id=c></div></div>"));
        CHECK(near(line_height_px(f.style("c"), 10, ctx), 20));
    }
    {
        Fixture f;
        CHECK(f.css("#p{font-size:20px;line-height:150%}#c{font:10px/2em monospace}"));
        CHECK(f.html("<div id=p><div id=c></div></div>"));
        CHECK(near(line_height_px(f.style("c"), 10, ctx), 20));
    }

    const int id = CssPropertyRegistry::instance().id_of("line-height");
    {
        Fixture f;
        CHECK(f.css("#p{font-size:20px;line-height:150%}#c{font-size:10px}"
            "#c::before{content:'x';font-size:5px}@layer first,second;"
            "@layer first{#c::before{line-height:2em}}@layer second{#c::before{line-height:revert-layer}}"));
        CHECK(f.html("<div id=p><div id=c></div></div>"));
        ComputedStyle before;
        CHECK(f.engine.compute_pseudo_element(*f.doc->get_element_by_id("c"), "before", f.state,
                                              *f.style("c"), &before));
        CHECK(near(line_height_px(&before, 5, ctx), 10));
    }
    ComputedStyle parent, child, own;
    parent.set("font-size", "20px"); parent.set(id, "150%");
    child.set_inherit_parent(&parent); child.set("font-size", "10px"); child.set(id, "150%");
    own.set_inherit_parent(&parent); own.set("font-size", "10px"); own.set(id, "150%");
    child.set_important(id, true); own.set_important(id, true);
    CHECK(near(line_height_px(&child, 10, ctx), 15));
    const auto first = child.version();
    child.mark_line_height_inherited();
    CHECK(child.version() != first && child.line_height_inherited() && child.is_important(id));
    CHECK(near(line_height_px(&child, 10, ctx), 30));
    std::vector<int> changed;
    bool unattributed = false;
    CHECK(child.differs_from(own, &changed, &unattributed));
    CHECK(!unattributed && changed.size() == 1 && changed.front() == id);
    parent.set(id, "2em");
    CHECK(near(line_height_px(&child, 10, ctx), 40));
    parent.set(id, "1.5");
    CHECK(near(line_height_px(&child, 10, ctx), 15));
    parent.set(id, "150%");
    const auto inherited_version = child.version();
    child.mark_line_height_inherited();
    CHECK(child.version() == inherited_version);
    child.set(id, "150%");
    CHECK(child.version() != inherited_version && !child.line_height_inherited());
    CHECK(near(line_height_px(&child, 10, ctx), 15));
    child.unset(id);
    CHECK(!child.line_height_inherited());
    CHECK(near(line_height_px(&child, 10, ctx), 30));
    CHECK(child.differs_from(own, nullptr, nullptr));
    // Changing a parent's font must not leave an inherited length at its old
    // value. Reparenting changes its base without modifying the child's syntax.
    for (const char* size : {"24px", "12px", "0px", "20px"}) {
        parent.set("font-size", size);
        CHECK(near(line_height_px(&child, 10, ctx), 1.5 * font_size_px(&parent, nullptr, ctx)));
    }
    own.set(id, "2em");
    child.set_inherit_parent(&own);
    CHECK(near(line_height_px(&child, 10, ctx), 20));
    child.set(id, "2em"); child.mark_line_height_inherited();
    ComputedStyle moved(std::move(child));
    CHECK(moved.line_height_inherited() && near(line_height_px(&moved, 5, ctx), 20));
    child.clear();
    CHECK(!child.line_height_inherited());
    child.set(id, "2em");
    CHECK(near(line_height_px(&child, 5, ctx), 10));

    // Ancestor lengths use current viewport/root/DPI inputs as well as the
    // current declaring font. Numbers/normal use the descendant's font.
    child.set_inherit_parent(&parent); child.unset(id);
    for (double w : {100., 200., 80., 100.}) {
        ctx.viewport_width_px = w;
        parent.set("font-size", "10vw"); parent.set(id, "150%");
        CHECK(near(line_height_px(&child, 5, ctx), w * .15));
        parent.set(id, "2rem"); ctx.root_font_size_px = w / 10;
        CHECK(near(line_height_px(&child, 5, ctx), w / 5));
        parent.set(id, "12pt"); ctx.dpi_pixels_per_inch = w;
        CHECK(near(line_height_px(&child, 5, ctx), w / 6));
    }
    MonoFontMetrics metrics(.5, 1.7, 1, .7);
    parent.set(id, "normal");
    CHECK(near(line_height_px(&child, 10, ctx, &metrics), 17));
    CHECK(near(line_height_px(nullptr, 10, ctx, &metrics), 17));
    std::vector<std::unique_ptr<ComputedStyle>> chain;
    const ComputedStyle* previous = &parent;
    parent.set("font-size", "20px"); parent.set(id, "150%");
    for (int i = 0; i < 256; ++i) {
        auto next = std::make_unique<ComputedStyle>(); next->set_inherit_parent(previous);
        next->set("font-size", "5px");
        previous = next.get(); chain.push_back(std::move(next));
    }
    CHECK(near(line_height_px(previous, 5, ctx), 30));
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

        // With cascade-time expansion the shorthand is gone by the time this
        // runs: all four longhands are set, the later `padding-left` wins its
        // own slot, and the other three keep the shorthand's value. This
        // function's shorthand branch is now reached only for a value the
        // expander refused, which in practice means one containing var().
        s = box_sides(f.style("mixed"), "padding");
        CHECK(s.left == "20px" && s.top == "5px" && s.right == "5px" && s.bottom == "5px");
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
