#include "check.h"
#include "weva/cascade.h"
#include "weva/dom.h"
#include "weva/html.h"
#include <memory>
#include <string>

using namespace weva;

namespace {
struct C {
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
    std::string colour(std::string_view element_id) {
        ComputedStyle cs;
        engine.compute(*id(element_id), state, nullptr, &cs);
        return std::string(cs.get("color"));
    }
};
} // namespace

void test_match_cache() {
    // ---- identical siblings share a cache entry
    {
        C c;
        CHECK(c.html("<div id=x class=k>1</div><div id=y class=k>2</div>"
                     "<div class=k>3</div><div class=k>4</div>"));
        CHECK(c.css(".k { color: red }"));
        c.engine.reset_cache_stats();
        for (auto* el : c.doc->get_elements_by_tag_name("div")) {
            c.engine.collect_matches(*el, c.state);
        }
        // #x and #y have distinct ids, so they key differently; the two
        // id-less .k divs are identical and must share.
        CHECK(c.engine.cache_stats().hits >= 1);
        CHECK(c.engine.cache_stats().skipped == 0);
    }

    // ---- a cached result must equal the uncached one
    {
        C c;
        CHECK(c.html("<div class=k>1</div><div class=k>2</div>"));
        CHECK(c.css(".k { color: red } div { color: blue }"));
        auto els = c.doc->get_elements_by_tag_name("div");
        auto first = c.engine.collect_matches(*els[0], c.state);
        auto second = c.engine.collect_matches(*els[1], c.state);
        CHECK(first.size() == second.size());
        for (std::size_t i = 0; i < first.size(); ++i) {
            CHECK(first[i].declaration->value_text == second[i].declaration->value_text);
        }
    }

    // ---- THE zebra-striping bug: :nth-child must not share across siblings.
    // Without folding sibling index into the key, every row gets row 1's
    // match set and the striping paints uniformly.
    {
        C c;
        CHECK(c.html("<ul><li id=r1>1</li><li id=r2>2</li><li id=r3>3</li></ul>"));
        CHECK(c.css("li { color: white } li:nth-child(odd) { color: grey }"));
        CHECK(c.colour("r1") == "grey");
        CHECK(c.colour("r2") == "white");
        CHECK(c.colour("r3") == "grey");
    }

    // ---- :first-child / :last-child likewise
    {
        C c;
        CHECK(c.html("<ul><li id=a>1</li><li id=b>2</li><li id=d>3</li></ul>"));
        CHECK(c.css("li { color: white } li:first-child { color: red } li:last-child { color: blue }"));
        CHECK(c.colour("a") == "red");
        CHECK(c.colour("b") == "white");
        CHECK(c.colour("d") == "blue");
    }

    // ---- sibling combinators disable sharing entirely: preceding-sibling
    // composition cannot be represented in a per-element key.
    {
        C c;
        CHECK(c.html("<div><p id=p1>1</p><p id=p2>2</p><span id=s>s</span><p id=p3>3</p></div>"));
        CHECK(c.css("p { color: white } p + p { color: red }"));
        CHECK(c.colour("p1") == "white");
        CHECK(c.colour("p2") == "red");
        CHECK(c.colour("p3") == "white");   // preceded by <span>, not <p>
        CHECK(c.engine.cache_stats().hits == 0);
        CHECK(c.engine.cache_stats().skipped > 0);
    }

    // ---- of-type pseudos also disable sharing
    {
        C c;
        CHECK(c.html("<div><span>s</span><p id=q1>1</p><p id=q2>2</p></div>"));
        CHECK(c.css("p { color: white } p:first-of-type { color: red }"));
        CHECK(c.colour("q1") == "red");
        CHECK(c.colour("q2") == "white");
    }

    // ---- :has() disables sharing: it depends on descendants the key cannot see
    {
        C c;
        CHECK(c.html("<div id=h1><b>x</b></div><div id=h2>y</div>"));
        CHECK(c.css("div { color: white } div:has(b) { color: red }"));
        CHECK(c.colour("h1") == "red");
        CHECK(c.colour("h2") == "white");
        CHECK(c.engine.cache_stats().skipped > 0);
    }

    // ---- inline style opts an element out (it is invisible to the key)
    {
        C c;
        CHECK(c.html("<div class=k>a</div><div class=k style='color: lime'>b</div>"));
        CHECK(c.css(".k { color: red }"));
        auto els = c.doc->get_elements_by_tag_name("div");
        c.engine.reset_cache_stats();
        c.engine.collect_matches(*els[0], c.state);
        c.engine.collect_matches(*els[1], c.state);
        CHECK(c.engine.cache_stats().skipped == 1);
    }

    // ---- an ANCESTOR's class changes the key even when the ancestor's own
    // match set does not. `.parent #c` matches only the descendant.
    {
        C c;
        CHECK(c.html("<div class=parent><span id=c1>x</span></div>"
                     "<div class=other><span id=c2>x</span></div>"));
        CHECK(c.css("span { color: white } .parent span { color: red }"));
        CHECK(c.colour("c1") == "red");
        CHECK(c.colour("c2") == "white");
    }

    // ---- attribute VALUES are part of the key, not just names
    {
        C c;
        CHECK(c.html("<div id=o data-state=open>a</div><div id=k data-state=closed>b</div>"));
        CHECK(c.css("div { color: white } [data-state=open] { color: red }"));
        CHECK(c.colour("o") == "red");
        CHECK(c.colour("k") == "white");
    }

    // ---- class token ORDER must not change the key
    {
        C c;
        CHECK(c.html("<div class='a b'>1</div><div class='b a'>2</div>"));
        CHECK(c.css(".a.b { color: red }"));
        auto els = c.doc->get_elements_by_tag_name("div");
        c.engine.reset_cache_stats();
        c.engine.collect_matches(*els[0], c.state);
        c.engine.collect_matches(*els[1], c.state);
        CHECK(c.engine.cache_stats().hits == 1);   // shared despite the reordering
    }
}
