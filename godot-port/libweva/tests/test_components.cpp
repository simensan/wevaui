// Weva components: `<template id="card">` declares one, `<card>` uses it, and
// `<slot>` inside the template is where the host's children land. Mirrors the
// reference's ComponentRegistry + ComponentExpander + SlotProjection, which
// UIDocumentBuilder runs before the cascade.
#include "check.h"
#include "weva/components.h"
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
