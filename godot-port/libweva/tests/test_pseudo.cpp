#include "check.h"
#include "weva/cascade.h"
#include "weva/dom.h"
#include "weva/html.h"
#include <memory>
#include <string>

using namespace weva;

namespace {
struct P {
    SymbolTable symbols;
    Ref<Document> doc;
    std::vector<std::unique_ptr<Stylesheet>> sheets;
    CascadeEngine engine;
    NullStateProvider state;

    bool html(std::string_view h) {
        HtmlParseError e; ParseOptions o; o.strict = false;
        doc = parse_html(h, &symbols, o, &e);
        return static_cast<bool>(doc);
    }
    bool css(std::string_view c) {
        auto s = std::make_unique<Stylesheet>();
        CssParseError e;
        if (!parse_stylesheet(c, false, s.get(), &e)) return false;
        engine.add_stylesheet(s.get(), DeclarationOrigin::Author);
        sheets.push_back(std::move(s));
        return true;
    }
    Element* id(std::string_view s) { return doc->get_element_by_id(s); }
};
} // namespace

void test_pseudo_elements() {
    // ---- a ::before rule produces a style; an element with no rule does not
    {
        P p;
        CHECK(p.html("<div id=a>x</div><div id=b>y</div>"));
        CHECK(p.css("#a::before { content: '>'; color: red }"));
        ComputedStyle host, pseudo;
        p.engine.compute(*p.id("a"), p.state, nullptr, &host);
        CHECK(p.engine.compute_pseudo_element(*p.id("a"), "before", p.state, host, &pseudo));
        CHECK(pseudo.get("color") == "red");

        ComputedStyle host_b, pseudo_b;
        p.engine.compute(*p.id("b"), p.state, nullptr, &host_b);
        // No rule targets #b::before — that means NO BOX, not an empty one.
        CHECK(!p.engine.compute_pseudo_element(*p.id("b"), "before", p.state, host_b, &pseudo_b));
    }

    // ---- ::before and ::after are separate buckets
    {
        P p;
        CHECK(p.html("<div id=a>x</div>"));
        CHECK(p.css("#a::before { content: 'B' } #a::after { content: 'A' }"));
        ComputedStyle host, before, after;
        p.engine.compute(*p.id("a"), p.state, nullptr, &host);
        CHECK(p.engine.compute_pseudo_element(*p.id("a"), "before", p.state, host, &before));
        CHECK(p.engine.compute_pseudo_element(*p.id("a"), "after", p.state, host, &after));
        std::string text;
        CHECK(CascadeEngine::resolve_pseudo_content(before, &text) && text == "B");
        CHECK(CascadeEngine::resolve_pseudo_content(after, &text) && text == "A");
    }

    // ---- a pseudo inherits from its ORIGINATING element, not the host's parent
    {
        P p;
        CHECK(p.html("<div id=outer><div id=a>x</div></div>"));
        CHECK(p.css("#outer { color: blue } #a { color: green } #a::before { content: 'x' }"));
        ComputedStyle outer, host, pseudo;
        p.engine.compute(*p.id("outer"), p.state, nullptr, &outer);
        p.engine.compute(*p.id("a"), p.state, &outer, &host);
        CHECK(host.get("color") == "green");
        CHECK(p.engine.compute_pseudo_element(*p.id("a"), "before", p.state, host, &pseudo));
        CHECK(pseudo.get("color") == "green");   // from #a, not #outer
    }

    // ---- an explicit pseudo declaration beats the inherited value
    {
        P p;
        CHECK(p.html("<div id=a>x</div>"));
        CHECK(p.css("#a { color: green } #a::before { content: 'x'; color: red }"));
        ComputedStyle host, pseudo;
        p.engine.compute(*p.id("a"), p.state, nullptr, &host);
        CHECK(p.engine.compute_pseudo_element(*p.id("a"), "before", p.state, host, &pseudo));
        CHECK(pseudo.get("color") == "red");
    }

    // ---- non-inherited properties fall to their initial, not the host's value
    {
        P p;
        CHECK(p.html("<div id=a>x</div>"));
        CHECK(p.css("#a { width: 100px } #a::before { content: 'x' }"));
        ComputedStyle host, pseudo;
        p.engine.compute(*p.id("a"), p.state, nullptr, &host);
        CHECK(p.engine.compute_pseudo_element(*p.id("a"), "before", p.state, host, &pseudo));
        CHECK(host.get("width") == "100px");
        CHECK(pseudo.get("width") == "auto");
    }

    // ---- a pseudo participates in the host's var() namespace
    {
        P p;
        CHECK(p.html("<div id=a>x</div>"));
        CHECK(p.css("#a { --accent: #0f0 } #a::before { content: 'x'; color: var(--accent) }"));
        ComputedStyle host, pseudo;
        p.engine.compute(*p.id("a"), p.state, nullptr, &host);
        CHECK(p.engine.compute_pseudo_element(*p.id("a"), "before", p.state, host, &pseudo));
        CHECK(pseudo.get("color") == "#0f0");
    }

    // ---- combinators and descendant selectors work on the originating element
    {
        P p;
        CHECK(p.html("<ul id=l><li id=one>1</li><li id=two>2</li></ul>"));
        CHECK(p.css("ul li::before { content: '-' } li:nth-child(2)::before { content: '+' }"));
        ComputedStyle h1, h2, p1, p2;
        p.engine.compute(*p.id("one"), p.state, nullptr, &h1);
        p.engine.compute(*p.id("two"), p.state, nullptr, &h2);
        CHECK(p.engine.compute_pseudo_element(*p.id("one"), "before", p.state, h1, &p1));
        CHECK(p.engine.compute_pseudo_element(*p.id("two"), "before", p.state, h2, &p2));
        std::string t;
        CHECK(CascadeEngine::resolve_pseudo_content(p1, &t) && t == "-");
        CHECK(CascadeEngine::resolve_pseudo_content(p2, &t) && t == "+");
    }

    // ---- specificity applies within a pseudo bucket
    {
        P p;
        CHECK(p.html("<div id=a class=c>x</div>"));
        CHECK(p.css("div::before { content: 'd' } .c::before { content: 'c' } #a::before { content: 'i' }"));
        ComputedStyle host, pseudo;
        p.engine.compute(*p.id("a"), p.state, nullptr, &host);
        CHECK(p.engine.compute_pseudo_element(*p.id("a"), "before", p.state, host, &pseudo));
        std::string t;
        CHECK(CascadeEngine::resolve_pseudo_content(pseudo, &t) && t == "i");
    }

    // ---- content resolution: `none`/`normal` suppress the box, `""` does not
    {
        P p;
        CHECK(p.html("<div id=a>x</div><div id=b>y</div><div id=c>z</div>"));
        CHECK(p.css("#a::before { content: none } #b::before { content: '' }"
                    "#c::before { content: attr(data-x) }"));
        ComputedStyle h, ps;
        std::string t;

        p.engine.compute(*p.id("a"), p.state, nullptr, &h);
        CHECK(p.engine.compute_pseudo_element(*p.id("a"), "before", p.state, h, &ps));
        CHECK(!CascadeEngine::resolve_pseudo_content(ps, &t));      // none -> no box

        p.engine.compute(*p.id("b"), p.state, nullptr, &h);
        CHECK(p.engine.compute_pseudo_element(*p.id("b"), "before", p.state, h, &ps));
        CHECK(CascadeEngine::resolve_pseudo_content(ps, &t) && t.empty());  // "" -> empty box

        // attr() is not supported in v1: report no box rather than rendering
        // the literal function text.
        p.engine.compute(*p.id("c"), p.state, nullptr, &h);
        CHECK(p.engine.compute_pseudo_element(*p.id("c"), "before", p.state, h, &ps));
        CHECK(!CascadeEngine::resolve_pseudo_content(ps, &t));
    }

    // ---- pseudo rules must NOT leak into ordinary element matching
    {
        P p;
        CHECK(p.html("<div id=a>x</div>"));
        CHECK(p.css("#a::before { color: red }"));
        ComputedStyle host;
        p.engine.compute(*p.id("a"), p.state, nullptr, &host);
        CHECK(host.get("color") != "red");
        CHECK(p.engine.collect_matches(*p.id("a"), p.state).empty());
    }
}
