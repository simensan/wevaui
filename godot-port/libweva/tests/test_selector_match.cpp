#include "check.h"
#include "weva/dom.h"
#include "weva/html.h"
#include "weva/selector.h"
#include <string>

using namespace weva;

namespace {

struct FakeState : ElementStateProvider {
    const Element* hovered = nullptr;
    ElementState state_of(const Element& e) const override {
        return (&e == hovered) ? ElementState::Hover : ElementState::None;
    }
};

struct M {
    SymbolTable symbols;
    Ref<Document> doc;
    NullStateProvider null_state;

    bool load(std::string_view html) {
        HtmlParseError e;
        ParseOptions o;
        o.strict = false;
        doc = parse_html(html, &symbols, o, &e);
        return static_cast<bool>(doc);
    }
    Element* id(std::string_view s) { return doc->get_element_by_id(s); }

    bool match(std::string_view sel, Element* e, const ElementStateProvider* st = nullptr) {
        CompiledSelector cs;
        SelectorParseError pe;
        if (!parse_selector(sel, &cs, &pe)) return false;
        return selector_matches(cs, *e, st ? *st : static_cast<const ElementStateProvider&>(null_state));
    }
    // Counts how many elements in the document a selector matches.
    int count(std::string_view sel, const ElementStateProvider* st = nullptr) {
        CompiledSelector cs;
        SelectorParseError pe;
        if (!parse_selector(sel, &cs, &pe)) return -1;
        int n = 0;
        walk(doc.get(), cs, st ? *st : static_cast<const ElementStateProvider&>(null_state), &n);
        return n;
    }
    void walk(Node* node, const CompiledSelector& cs, const ElementStateProvider& st, int* n) {
        for (const auto& c : node->children()) {
            if (c->is_element() && selector_matches(cs, *static_cast<Element*>(c.get()), st)) ++(*n);
            walk(c.get(), cs, st, n);
        }
    }
};

} // namespace

void test_selector_match() {
    M m;
    CHECK(m.load(
        "<div id=root class='page wide'>"
        "  <h1 id=title>T</h1>"
        "  <p id=p1 class='lead'>a</p>"
        "  <p id=p2 class='lead hot' data-k='x-y' lang='en-GB'>b</p>"
        "  <ul id=list><li id=l1>1</li><li id=l2 class=sel>2</li><li id=l3>3</li></ul>"
        "  <a id=link href='#x'>go</a>"
        "  <span id=empty></span>"
        "</div>"));

    // ---- simple selectors
    CHECK(m.match("div", m.id("root")));
    CHECK(m.match("#root", m.id("root")));
    CHECK(m.match(".page", m.id("root")));
    CHECK(m.match(".wide", m.id("root")));      // second token in the class list
    CHECK(!m.match(".pag", m.id("root")));      // must not prefix-match
    CHECK(m.match("*", m.id("root")));
    CHECK(!m.match("span", m.id("root")));

    // ---- combinators
    CHECK(m.match("div > h1", m.id("title")));
    CHECK(m.match("div h1", m.id("title")));
    CHECK(!m.match("ul > li > p", m.id("p1")));
    CHECK(m.match("#p1 + #p2", m.id("p2")));
    CHECK(!m.match("#title + #p2", m.id("p2")));   // adjacent means immediately
    CHECK(m.match("#title ~ #p2", m.id("p2")));    // general sibling spans
    CHECK(m.match("body div > ul li", m.id("l2")));

    // ---- structural pseudo-classes
    CHECK(m.match(":first-child", m.id("l1")));
    CHECK(m.match(":last-child", m.id("l3")));
    CHECK(!m.match(":first-child", m.id("l2")));
    CHECK(m.match(":only-child", m.id("title")) == false);   // has siblings
    CHECK(m.match("li:nth-child(2)", m.id("l2")));
    CHECK(m.match("li:nth-child(odd)", m.id("l1")));
    CHECK(m.match("li:nth-child(odd)", m.id("l3")));
    CHECK(!m.match("li:nth-child(odd)", m.id("l2")));
    CHECK(m.match("li:nth-last-child(1)", m.id("l3")));
    CHECK(m.match("p:first-of-type", m.id("p1")));
    CHECK(m.match("p:last-of-type", m.id("p2")));
    CHECK(m.match("p:nth-of-type(2)", m.id("p2")));
    CHECK(m.match(":empty", m.id("empty")));
    CHECK(!m.match(":empty", m.id("title")));      // has a text child

    // ---- :root is the element whose parent is the Document
    CHECK(m.count(":root") == 1);
    CHECK(m.match(":root", static_cast<Element*>(m.doc->children()[0].get())));

    // ---- attribute selectors
    CHECK(m.match("[data-k]", m.id("p2")));
    CHECK(m.match("[data-k=x-y]", m.id("p2")));
    CHECK(!m.match("[data-k=X-Y]", m.id("p2")));       // ordinal by default
    CHECK(m.match("[data-k=X-Y i]", m.id("p2")));      // ...unless flagged
    CHECK(m.match("[data-k^=x]", m.id("p2")));
    CHECK(m.match("[data-k$=y]", m.id("p2")));
    CHECK(m.match("[data-k*=-]", m.id("p2")));
    CHECK(m.match("[class~=hot]", m.id("p2")));
    CHECK(!m.match("[class~=ho]", m.id("p2")));
    CHECK(m.match("[lang|=en]", m.id("p2")));          // en-GB matches en
    CHECK(!m.match("[lang|=e]", m.id("p2")));          // but not a partial subtag

    // ---- :not / :is / :where
    CHECK(m.match("p:not(.hot)", m.id("p1")));
    CHECK(!m.match("p:not(.hot)", m.id("p2")));
    CHECK(m.match(":is(h1, p)", m.id("title")));
    CHECK(m.match(":where(h1, p)", m.id("p1")));
    CHECK(m.match("p:not(.hot, .nope)", m.id("p1")));
    CHECK(!m.match("p:not(.lead)", m.id("p1")));       // :not with a list is AND-of-nones

    // ---- :has, including the relative-selector anchor
    CHECK(m.match("ul:has(> li)", m.id("list")));
    CHECK(m.match("ul:has(.sel)", m.id("list")));
    CHECK(!m.match("ul:has(> p)", m.id("list")));
    CHECK(m.match("#title:has(+ p)", m.id("title")));
    CHECK(m.match("#title:has(~ #link)", m.id("title")));
    CHECK(!m.match("#l3:has(+ li)", m.id("l3")));      // nothing after l3
    // :has must not escape upward: #l1 has no ancestor search
    CHECK(!m.match("#l1:has(ul)", m.id("l1")));

    // ---- :lang walks up to the nearest lang attribute
    CHECK(m.match(":lang(en)", m.id("p2")));
    CHECK(m.match(":lang(en-GB)", m.id("p2")));
    CHECK(!m.match(":lang(fr)", m.id("p2")));

    // ---- links
    CHECK(m.match("a:any-link", m.id("link")));
    CHECK(m.match("a:link", m.id("link")));
    CHECK(!m.match("a:visited", m.id("link")));        // no history in a headless host

    // ---- interaction state comes from the provider
    {
        FakeState st;
        st.hovered = m.id("p2");
        CHECK(m.match(":hover", m.id("p2"), &st));
        CHECK(!m.match(":hover", m.id("p1"), &st));
        CHECK(m.match("p.lead:hover", m.id("p2"), &st));
        CHECK(m.count(":hover", &st) == 1);
        // :enabled is the absence of the disabled bit
        CHECK(m.match(":enabled", m.id("p1"), &st));
    }

    // ---- a selector ending in a pseudo-element never matches the element
    CHECK(!m.match("div::before", m.id("root")));
    CHECK(!m.match("::before", m.id("root")));

    // ---- counts across the document
    CHECK(m.count("li") == 3);
    CHECK(m.count("p.lead") == 2);
    CHECK(m.count("li:nth-child(odd)") == 2);
    CHECK(m.count("[href]") == 1);
}
