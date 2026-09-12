// Weva components: `<template id="card">` declares one, `<card>` uses it, and
// `<slot>` inside the template is where the host's children land. Mirrors the
// reference's ComponentRegistry + ComponentExpander + SlotProjection, which
// UIDocumentBuilder runs before the cascade.
#include "check.h"
#include "weva/components.h"
#include "weva/component_scoping.h"
#include "weva/css_rule.h"
#include "weva/dom.h"
#include "weva/html.h"

#include <string>
#include <vector>

using namespace weva;

namespace {

struct Fixture {
    SymbolTable symbols;
    Ref<Document> doc;

    bool parse(std::string_view html) {
        HtmlParseError err;
        ParseOptions o;
        o.strict = false;
        doc = parse_html(html, &symbols, o, &err);
        if (!doc) return false;
        expand_components(doc.get());
        return true;
    }

    // First element with this tag anywhere in the tree, in document order.
    const Element* find(std::string_view tag, const Node* from = nullptr) const {
        const Node* start = from ? from : static_cast<const Node*>(doc.get());
        if (start->node_type() == NodeType::Element) {
            const auto* e = static_cast<const Element*>(start);
            std::string lowered;
            for (char c : e->tag_name()) {
                lowered += (c >= 'A' && c <= 'Z') ? static_cast<char>(c - 'A' + 'a') : c;
            }
            if (lowered == tag) return e;
        }
        for (const Ref<Node>& c : start->children()) {
            if (const Element* hit = find(tag, c.get())) return hit;
        }
        return nullptr;
    }

    // Concatenated text of a subtree, whitespace and all.
    std::string text_of(const Node* n) const {
        if (!n) return {};
        if (n->node_type() == NodeType::Text) return static_cast<const TextNode*>(n)->data();
        std::string out;
        for (const Ref<Node>& c : n->children()) out += text_of(c.get());
        return out;
    }

    // Counts RENDERED elements: a `<template>` body is source that stays in
    // the document, so counting into it would count every slot and clone
    // source twice over.
    int count(std::string_view tag, const Node* from = nullptr) const {
        const Node* start = from ? from : static_cast<const Node*>(doc.get());
        int n = 0;
        if (start->node_type() == NodeType::Element) {
            const auto* e = static_cast<const Element*>(start);
            std::string lowered;
            for (char c : e->tag_name()) {
                lowered += (c >= 'A' && c <= 'Z') ? static_cast<char>(c - 'A' + 'a') : c;
            }
            if (lowered == "template") return 0;
            if (lowered == tag) ++n;
        }
        for (const Ref<Node>& c : start->children()) n += count(tag, c.get());
        return n;
    }
};

} // namespace

void test_component_expansion() {
    {
        // The shape card-component.html uses: named slots, a default slot, and
        // the host's children projected into them.
        Fixture f;
        CHECK(f.parse(
            "<template id=card><article class=card>"
            "<header><slot name=title>Untitled</slot></header>"
            "<section class=body><slot>No content.</slot></section>"
            "<footer><slot name=footer></slot></footer>"
            "</article></template>"
            "<card><span slot=title>Welcome</span><p>Body text.</p>"
            "<button slot=footer>Got it</button></card>"));

        // The host is expanded in place: the article is inside <card>.
        const Element* host = f.find("card");
        CHECK(host != nullptr);
        CHECK(host->has_attribute("data-uui-expanded"));
        const Element* article = f.find("article");
        CHECK(article != nullptr);

        // Every slot is gone, replaced by what was projected into it.
        CHECK(f.count("slot") == 0);
        CHECK_EQ(f.text_of(f.find("header")), "Welcome");
        CHECK_EQ(f.text_of(f.find("section")), "Body text.");
        CHECK_EQ(f.text_of(f.find("footer")), "Got it");
    }
    {
        // A slot with nothing projected falls back to its own content; a slot
        // with neither disappears entirely.
        Fixture f;
        CHECK(f.parse(
            "<template id=box><div class=b>"
            "<span class=t><slot name=title>Untitled</slot></span>"
            "<span class=e><slot name=extra></slot></span>"
            "</div></template>"
            "<box></box>"));
        CHECK(f.count("slot") == 0);
        CHECK_EQ(f.text_of(f.find("span")), "Untitled");
    }
    {
        // Two default slots: the second cannot take the same nodes — appending
        // them again would re-parent them out of the first — so it gets a copy.
        Fixture f;
        CHECK(f.parse(
            "<template id=two><div><p class=a><slot></slot></p>"
            "<p class=b><slot></slot></p></div></template>"
            "<two><em>hi</em></two>"));
        CHECK(f.count("em") == 2);
        CHECK(f.count("slot") == 0);
    }
    {
        // A template body is source, never rendered in place, and it is moved
        // to the end of its parent so a document-order search finds the
        // expanded clone first.
        Fixture f;
        CHECK(f.parse("<template id=t><i>x</i></template><t></t>"));
        const Node* body = f.find("body");
        CHECK(body != nullptr);
        const Element* last = nullptr;
        for (const Ref<Node>& c : body->children()) {
            if (c->node_type() == NodeType::Element) last = static_cast<const Element*>(c.get());
        }
        CHECK(last != nullptr);
        CHECK_EQ(last->tag_name(), "template");
    }
    {
        // A template whose body root repeats the HOST's tag is a render
        // output, not another instance: `<template id=button><button
        // class=btn><slot></slot></button></template>` must render exactly one
        // <button>, not recurse until the depth guard fires.
        Fixture f;
        CHECK(f.parse(
            "<template id=button><button class=btn><slot></slot></button></template>"
            "<button>Go</button>"));
        CHECK(f.count("button") == 2);  // the host, and its one rendered clone
    }
    {
        // A BARE self-cycle has no render content, so the depth guard has to
        // catch it rather than the same-tag rule. It must terminate.
        Fixture f;
        CHECK(f.parse("<template id=loop><loop></loop></template><loop></loop>"));
        CHECK(f.count("loop") > 0);
    }
    {
        // An unknown element that names no template is left completely alone.
        Fixture f;
        CHECK(f.parse("<template id=card><b>x</b></template><widget><i>y</i></widget>"));
        const Element* w = f.find("widget");
        CHECK(w != nullptr);
        CHECK(!w->has_attribute("data-uui-expanded"));
        CHECK_EQ(f.text_of(w), "y");
    }
}

// A `<style>` inside a template is the component's own sheet: the clone is
// stamped with the scope, the host with the host marker, slotted light-dom
// with neither; and the selector rewrite reads exactly as the reference's.
void test_component_scoped_styles() {
    const std::string id = "card";
    const auto one = [&](const char* sel) {
        const std::vector<std::string> out = scope_selector_list(sel, id);
        return out.size() == 1 ? out[0] : std::string("<") + std::to_string(out.size()) + ">";
    };
    CHECK(one(".foo") == ".foo[data-uui-scope=\"card\"]");
    CHECK(one("div") == "div[data-uui-scope=\"card\"]");
    CHECK(one("div.foo") == "div.foo[data-uui-scope=\"card\"]");
    CHECK(one("a b") == "a b[data-uui-scope=\"card\"]");
    CHECK(one("a > b") == "a > b[data-uui-scope=\"card\"]");
    CHECK(one("a>b") == "a > b[data-uui-scope=\"card\"]");
    CHECK(one("a + b") == "a + b[data-uui-scope=\"card\"]");
    CHECK(one("a ~ b") == "a ~ b[data-uui-scope=\"card\"]");
    {
        const std::vector<std::string> two = scope_selector_list("a, b", id);
        CHECK(two.size() == 2 && two[0] == "a[data-uui-scope=\"card\"]" && two[1] == "b[data-uui-scope=\"card\"]");
    }
    CHECK(one(":host") == "[data-uui-host=\"card\"]");
    CHECK(one(":host.disabled") == "[data-uui-host=\"card\"].disabled");
    CHECK(one(":host(.disabled)") == "[data-uui-host=\"card\"].disabled");
    CHECK(one(":host(:hover)") == "[data-uui-host=\"card\"]:hover");
    {
        const std::vector<std::string> alts = scope_selector_list(":host(.a, .b)", id);
        CHECK(alts.size() == 2 && alts[0] == "[data-uui-host=\"card\"].a" && alts[1] == "[data-uui-host=\"card\"].b");
        const std::vector<std::string> deep = scope_selector_list(":host(.a, .b) > .item", id);
        CHECK(deep.size() == 2 && deep[0] == "[data-uui-host=\"card\"].a > .item[data-uui-scope=\"card\"]");
        CHECK(deep.size() == 2 && deep[1] == "[data-uui-host=\"card\"].b > .item[data-uui-scope=\"card\"]");
    }
    CHECK(one("a::before") == "a[data-uui-scope=\"card\"]::before");
    CHECK(one("a:hover") == "a[data-uui-scope=\"card\"]:hover");
    CHECK(one("a > b:hover") == "a > b[data-uui-scope=\"card\"]:hover");
    CHECK(one(":not(.x)") == ":not(.x)[data-uui-scope=\"card\"]");
    CHECK(one("a[title=\"x > y\"]") == "a[title=\"x > y\"][data-uui-scope=\"card\"]");

    // The sheet rewrite descends into @media and leaves @keyframes alone.
    {
        Stylesheet sheet;
        CssParseError err;
        CHECK(parse_stylesheet(".t { color: red } @media (min-width: 1px) { .u:hover { color: blue } }"
                               "@keyframes k { from { opacity: 0 } to { opacity: 1 } }", false, &sheet, &err));
        scope_stylesheet(&sheet, id);
        CHECK(sheet.rules.size() == 3);
        const auto* top = static_cast<const StyleRule*>(sheet.rules[0].get());
        CHECK(top->selectors.size() == 1 && top->selectors[0] == ".t[data-uui-scope=\"card\"]");
        const auto* media = static_cast<const GenericAtRule*>(sheet.rules[1].get());
        CHECK(media->nested_rules.size() == 1);
        const auto* inner = static_cast<const StyleRule*>(media->nested_rules[0].get());
        CHECK(inner->selectors.size() == 1 && inner->selectors[0] == ".u[data-uui-scope=\"card\"]:hover");
        const auto* frames = static_cast<const GenericAtRule*>(sheet.rules[2].get());
        CHECK(frames->name == "keyframes" && frames->prelude == "k");
    }

    // Expansion: the sheet is reported, clones stamped, light-dom not.
    {
        SymbolTable symbols;
        HtmlParseError err;
        ParseOptions o;
        o.strict = false;
        Ref<Document> doc = parse_html(
            "<template id=card><style>.title { color: red }</style>"
            "<div class=frame><span class=title>t</span><slot></slot></div></template>"
            "<template id=plain><b>p</b></template>"
            "<card><em class=title>light</em></card><plain></plain>", &symbols, o, &err);
        CHECK(doc.get() != nullptr);
        std::vector<ComponentStylesheet> sheets;
        expand_components(doc.get(), &sheets);
        CHECK(sheets.size() == 1 && sheets[0].tag == "card" && sheets[0].scope_id == "card");
        CHECK(sheets[0].css.find(".title { color: red }") != std::string::npos);
        Fixture f;
        f.doc = doc;
        const Element* host = f.find("card");
        CHECK(host && host->get_attribute("data-uui-host") == "card");
        const Element* frame = f.find("div", host);
        CHECK(frame && frame->get_attribute("data-uui-scope") == "card");
        const Element* title = f.find("span", host);
        CHECK(title && title->get_attribute("data-uui-scope") == "card");
        const Element* light = f.find("em", host);
        CHECK(light && !light->has_attribute("data-uui-scope"));
        // The sheet is not cloned into the instance.
        CHECK(f.count("style", host) == 0);
        // A template without a style stamps nothing.
        const Element* plain = f.find("plain");
        CHECK(plain && !plain->has_attribute("data-uui-host"));
        const Element* b = f.find("b", plain);
        CHECK(b && !b->has_attribute("data-uui-scope"));
    }
}
