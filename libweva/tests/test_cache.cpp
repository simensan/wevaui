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
        // Unreferenced action IDs do not change stylesheet matches.
        CHECK(c.engine.cache_stats().hits == 3);
        CHECK(c.engine.cache_stats().skipped == 0);
    }

    // The same applies to IDs on ancestors, but nested selectors, a later
    // stylesheet, and ID mutation must still distinguish observed values.
    {
        C c;
        CHECK(c.html("<section id=one><div id=a></div></section><section id=two><div id=b></div></section>"));
        CHECK(c.css("div { color: red }"));
        CHECK(c.colour("a") == "red");
        c.engine.reset_cache_stats();
        CHECK(c.colour("b") == "red");
        CHECK(c.engine.cache_stats().hits == 1);
        CHECK(c.css(":is(#one) > div:not(#b) { color: blue }"));
        CHECK(c.colour("a") == "blue");
        CHECK(c.colour("b") == "red");
        c.id("one")->set_attribute("id", "other");
        CHECK(c.colour("a") == "red");
        CHECK(c.css("[id^=oth] > div { color: lime }"));
        CHECK(c.colour("a") == "lime");
        CHECK(c.colour("b") == "red");
    }
    {
        C c;
        CHECK(c.html("<div></div><div id=''></div><div id=x></div>"));
        CHECK(c.css("div { color: red } [id] { color: blue } [id=x] { color: lime }"));
        const auto elements = c.doc->get_elements_by_tag_name("div");
        const char* colors[] = {"red", "blue", "lime"};
        for (size_t i = 0; i < elements.size(); ++i) {
            ComputedStyle style;
            c.engine.compute(*elements[i], c.state, nullptr, &style);
            CHECK(style.get("color") == colors[i]);
        }
    }

    // Exact metadata predicates only distinguish mentioned values. This
    // includes predicates on ancestors, empty values and presence checks.
    {
        C c;
        CHECK(c.html("<section data-key=row-a><div id=a data-mode=one></div></section>"
                     "<section data-key=row-b><div id=b data-mode=two></div></section>"
                     "<section data-key=''><div id=z></div></section>"));
        CHECK(c.css("div{color:red} [data-key=selected] div{color:blue}"
                    "[data-mode]{width:7px} [data-key=''] div{color:lime}"));
        CHECK(c.colour("a") == "red");
        c.engine.reset_cache_stats();
        CHECK(c.colour("b") == "red");
        CHECK(c.engine.cache_stats().hits == 1);
        CHECK(c.colour("z") == "lime");
        auto* parent = static_cast<Element*>(c.id("b")->parent());
        parent->set_attribute("data-key", "selected");
        CHECK(c.colour("b") == "blue");
        parent->set_attribute("data-key", "row-b");
        CHECK(c.colour("b") == "red");
        CHECK(c.css("[data-key$='b'] div{color:yellow} [data-mode=ONE i]{color:purple}"));
        CHECK(c.colour("a") == "purple");
        CHECK(c.colour("b") == "yellow");
        ComputedStyle absent;
        c.engine.compute(*c.id("z"), c.state, nullptr, &absent);
        CHECK(absent.get("width") != "7px");
    }

    // A positional rule for a caption must not scan sibling lists or split
    // cache entries for unrelated controls. The matching subject still folds
    // its rank/content and remains correct after mutation.
    {
        C c;
        CHECK(c.html("<main><button id=a>A</button><button id=b>B</button>"
                     "<figcaption id=empty></figcaption><figcaption id=full>text</figcaption></main>"));
        CHECK(c.css("button,figcaption{color:red} figcaption:empty{color:blue}"));
        CHECK(c.colour("a") == "red");
        c.engine.reset_cache_stats();
        CHECK(c.colour("b") == "red");
        CHECK(c.engine.cache_stats().hits == 1);
        CHECK(c.colour("empty") == "blue");
        CHECK(c.colour("full") == "red");
        CHECK(c.css("button:is(:first-child){color:lime}"));
        CHECK(c.colour("a") == "lime");
        CHECK(c.colour("b") == "red");
    }

    // Attribute order may commute; the association between a name and its
    // value must not. Swapping booleans used to hide an open tool by sharing
    // the closed tool's stylesheet matches. Ancestor keys need the same rule.
    {
        C c;
        CHECK(c.html("<section id=a data-open=false data-movable=true><div id=x></div></section>"
                     "<section id=b data-open=true data-movable=false><div id=y></div></section>"));
        CHECK(c.css("section,div{color:red} [data-open=true]{color:blue}"
                    "[data-movable=true] > div{color:lime}"));
        CHECK(c.colour("a") == "red");
        CHECK(c.colour("b") == "blue");
        CHECK(c.colour("x") == "lime");
        CHECK(c.colour("y") == "red");
        c.id("b")->set_attribute("data-open", "false");
        c.id("b")->set_attribute("data-movable", "true");
        CHECK(c.colour("b") == "red");
        CHECK(c.colour("y") == "lime");
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

    // ---- sibling combinators disable sharing for their possible subjects:
    // composition cannot be represented in a per-element key.
    {
        C c;
        CHECK(c.html("<div><p id=p1>1</p><p id=p2>2</p><span id=s>s</span><p id=p3>3</p></div>"));
        CHECK(c.css("p { color: white } p + p { color: red } span { color: blue }"));
        CHECK(c.colour("p1") == "white");
        CHECK(c.colour("p2") == "red");
        CHECK(c.colour("p3") == "white");   // preceded by <span>, not <p>
        CHECK(c.engine.cache_stats().hits == 0);
        CHECK(c.engine.cache_stats().skipped > 0);
        CHECK(c.colour("s") == "blue");
        c.engine.reset_cache_stats();
        CHECK(c.colour("s") == "blue");
        CHECK(c.engine.cache_stats().hits == 1);
        CHECK(c.engine.cache_stats().skipped == 0);
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

    // A keyed :has subject must not disable unrelated match-cache entries.
    for (const char* selector : {"form:has(.bad)", ".panel:has(.bad)", "#f:has(.bad)"}) {
        C c;
        CHECK(c.html("<form id=f class=panel><input id=a><input id=b></form>"));
        CHECK(c.css(std::string("form,input{color:white}") + selector + "{color:red}"));
        CHECK(c.colour("a") == "white");
        c.engine.reset_cache_stats();
        CHECK(c.colour("a") == "white");
        CHECK(c.engine.cache_stats().hits == 1);
        CHECK(c.engine.cache_stats().skipped == 0);
        CHECK(c.colour("f") == "white");
        c.id("b")->set_attribute("class", "bad");
        CHECK(c.colour("f") == "red");
        CHECK(c.colour("a") == "white");
        c.id("b")->remove_attribute("class");
        CHECK(c.colour("f") == "white");
    }
    // A :has on an ancestor affects the rightmost subject, not its own tag.
    {
        C c;
        CHECK(c.html("<form id=f><input id=a><input id=b></form>"));
        CHECK(c.css("input{color:white}form:has(.bad) input{color:red}"));
        CHECK(c.colour("a") == "white");
        c.id("b")->set_attribute("class", "bad");
        CHECK(c.colour("a") == "red");
        c.id("b")->remove_attribute("class");
        CHECK(c.colour("a") == "white");
    }
    // Nested/unkeyed subjects remain conservative, including negative matching.
    for (const char* selector : {":is(form:has(.bad),input)", ":not(:has(.bad))"}) {
        C c;
        CHECK(c.html("<form id=f><input id=a></form>"));
        CHECK(c.css(std::string(selector) + "{color:red}"));
        c.engine.collect_matches(*c.id("a"), c.state);
        c.engine.reset_cache_stats();
        c.engine.collect_matches(*c.id("a"), c.state);
        CHECK(c.engine.cache_stats().skipped == 1);
    }

    // Inline values and binding metadata do not change stylesheet matches.
    // Their own declarations are still applied independently after the hit.
    {
        C c;
        CHECK(c.html("<section style='left:1px' data-key=a title=A><div class=k>a</div></section>"
                     "<section style='left:2px' data-key=b title=B><div class=k style='color: lime' data-each=Items on-click=Pick>b</div></section>"));
        CHECK(c.css(".k { color: red }"));
        auto els = c.doc->get_elements_by_tag_name("div");
        c.engine.reset_cache_stats();
        c.engine.collect_matches(*els[0], c.state);
        c.engine.collect_matches(*els[1], c.state);
        CHECK(c.engine.cache_stats().skipped == 0);
        CHECK(c.engine.cache_stats().hits == 1);
        ComputedStyle plain, inline_style;
        c.engine.compute(*els[0], c.state, nullptr, &plain);
        c.engine.compute(*els[1], c.state, nullptr, &inline_style);
        CHECK(plain.get("color") == "red" && inline_style.get("color") == "lime");

        // A later stylesheet can start observing an ignored attribute, even
        // inside functional selectors and on ancestors. Old matches retire.
        CHECK(c.css(":is([data-key=b], [title=A]) > div { background-color: blue }"
                    "section[style='left:2px'] > div { border-top-width: 9px }"
                    "div:not([data-each=Items]) { width: 7px }"
                    "[on-click=Pick] { height: 11px }"));
        c.engine.compute(*els[0], c.state, nullptr, &plain);
        c.engine.compute(*els[1], c.state, nullptr, &inline_style);
        CHECK(plain.get("background-color") == "blue" && inline_style.get("background-color") == "blue");
        CHECK(plain.get("border-top-width") != "9px" && inline_style.get("border-top-width") == "9px");
        CHECK(plain.get("width") == "7px" && inline_style.get("width") != "7px");
        CHECK(plain.get("height") != "11px" && inline_style.get("height") == "11px");
        els[1]->set_attribute("data-each", "Other");
        c.engine.compute(*els[1], c.state, nullptr, &inline_style);
        CHECK(inline_style.get("width") == "7px");
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
