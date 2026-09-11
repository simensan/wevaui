#include "check.h"
#include "weva/dom.h"
#include "weva/html.h"
#include <string>

using namespace weva;

namespace {

struct P {
    SymbolTable symbols;
    HtmlParseError error;
    Ref<Document> doc;

    bool run(std::string_view src, bool strict = true) {
        ParseOptions o;
        o.strict = strict;
        doc = parse_html(src, &symbols, o, &error);
        return static_cast<bool>(doc);
    }
};

// "div>span#x.cls" style shape dump, so structural expectations read at a glance.
std::string shape(Node* n) {
    std::string out;
    for (const auto& c : n->children()) {
        if (c->is_element()) {
            auto* e = static_cast<Element*>(c.get());
            out += e->tag_name();
            if (!e->id().empty()) out += "#" + std::string(e->id());
            if (!c->children().empty()) out += "(" + shape(c.get()) + ")";
        } else {
            out += "'" + static_cast<TextNode*>(c.get())->data() + "'";
        }
        out += " ";
    }
    if (!out.empty()) out.pop_back();
    return out;
}

} // namespace

void test_html_parser() {
    // ---- a bare fragment gets the full browser shape: html > head + body.
    //
    // The empty <head> is load-bearing for structural selectors: without it
    // <body> is :nth-child(1) rather than :nth-child(2). Fixed in both engines
    // (HtmlParser.cs EnsureBody now calls EnsureHead).
    {
        P p;
        CHECK(p.run("<main class=\"hud\">hi</main>"));
        CHECK(shape(p.doc.get()) == "html(head body(main('hi')))");
    }

    // ---- the empty <head> exists so <body> is the SECOND child. This is the
    // whole reason for the fix; a shape assertion alone would not catch a
    // regression that emitted <head> after <body>.
    {
        P p;
        CHECK(p.run("<div>x</div>"));
        auto* html = static_cast<Element*>(p.doc->children()[0].get());
        CHECK(html->tag_name() == "html");
        CHECK(html->children().size() == 2);
        CHECK(static_cast<Element*>(html->children()[0].get())->tag_name() == "head");
        CHECK(static_cast<Element*>(html->children()[1].get())->tag_name() == "body");
    }

    // ---- head content still routes correctly now that head is unconditional
    {
        P p;
        CHECK(p.run("<link rel=stylesheet href=a.css><div>x</div>"));
        CHECK(shape(p.doc.get()) == "html(head(link) body(div('x')))");
    }

    // ---- an explicit <html> is trusted verbatim, no synthesis
    {
        P p;
        CHECK(p.run("<html><body><div>x</div></body></html>"));
        CHECK(shape(p.doc.get()) == "html(body(div('x')))");
    }

    // ---- empty / whitespace / doctype-only keep the zero-element contract
    {
        P p;
        CHECK(p.run(""));
        CHECK(p.doc->children().empty());
        // Whitespace-only skips fragment synthesis (no wrappers), but the
        // text token still reaches BuildTree and becomes a child of Document.
        // "empty Document" in the C# comment means no element wrappers.
        P p2;
        CHECK(p2.run("   \n\t "));
        CHECK(p2.doc->children().size() == 1);
        CHECK(!p2.doc->children()[0]->is_element());
        P p3;
        CHECK(p3.run("<!DOCTYPE html>"));
        CHECK(p3.doc->children().empty());
    }

    // ---- head-content routing, and the <style> text bug this guards against:
    // text inside an open style must not close <head> mid-element.
    {
        P p;
        CHECK(p.run("<style>.a{color:red}</style><div>content</div>"));
        CHECK(shape(p.doc.get()) == "html(head(style('.a{color:red}')) body(div('content')))");
    }

    // ---- <template> is body content, not head (Weva binding blocks)
    {
        P p;
        CHECK(p.run("<template>x</template>"));
        CHECK(shape(p.doc.get()) == "html(head body(template('x')))");
    }

    // ---- implicit close: <p>One<p>Two are siblings, not nested
    {
        P p;
        CHECK(p.run("<p>One<p>Two"));
        CHECK(shape(p.doc.get()) == "html(head body(p('One') p('Two')))");
    }

    // ---- void elements never open a scope
    {
        P p;
        CHECK(p.run("<div><br><img src=a.png>after</div>"));
        CHECK(shape(p.doc.get()) == "html(head body(div(br img 'after')))");
    }

    // ---- <li> closes on <li>; the <ul> scope is not crossed
    {
        P p;
        CHECK(p.run("<ul><li>a<li>b</ul>"));
        CHECK(shape(p.doc.get()) == "html(head body(ul(li('a') li('b'))))");
    }

    // ---- optional-close scope guard. Without it the </li> is mis-attributed
    // to the outer <li> and the pop loop raises a fatal mismatch.
    {
        P p;
        CHECK(p.run("<ul><li><ul><span></span></li></ul></ul>"));
        CHECK(p.error.message.empty());
    }

    // ---- adoption agency. The <p> is closed, the <a> is popped onto the
    // active-formatting list and reconstructed inside the <div>, and the
    // trailing </p> finds no match (body and html are both optional-close, so
    // the scope search runs off the stack) and inserts an empty <p>.
    //
    // NOTE: this is NOT Chrome's DOM. Chrome produces
    //   <p>Click <a></a></p> <a><div>here</div></a> <a> to start</a> <p></p>
    // i.e. the <a> wraps the <div> and also wraps the trailing text. The C#
    // AAA-lite instead nests <a> *inside* the <div> and leaves " to start"
    // unwrapped, because </a> clears the AFL before the text arrives. The C#
    // comment claims Chrome parity; the algorithm as written does not deliver
    // it. Reproduced faithfully — fixing it here would register as oracle
    // divergence. Flagged in PORT_PLAN.md.
    {
        P p;
        CHECK(p.run("<p>Click <a href=\"#\"><div>here</div></a> to start</p>"));
        CHECK(shape(p.doc.get()) ==
              "html(head body(p('Click ' a) div(a('here')) ' to start' p))");
    }

    // ---- a reconstructed formatting element clones its attributes
    {
        P p;
        CHECK(p.run("<p><b class=\"hl\"><div>x</div></b>tail</p>"));
        auto bolds = p.doc->get_elements_by_class_name("hl");
        CHECK(bolds.size() == 2);            // the original, plus the clone in <div>
        for (auto* b : bolds) CHECK(b->get_attribute("class") == "hl");
    }

    // ---- strict mode reports, never throws
    {
        P p;
        CHECK(!p.run("<div><span></div>"));
        CHECK(p.error.message.find("Mismatched end tag") != std::string::npos);

        P p2;
        CHECK(!p2.run("<div>"));
        CHECK(p2.error.message == "Unclosed element 'div'");

        P p3;
        CHECK(!p3.run("</br>"));
        CHECK(p3.error.message.find("void element") != std::string::npos);
    }

    // ---- lenient mode absorbs the same input
    {
        P p;
        CHECK(p.run("<div><span></div>", /*strict=*/false));
        CHECK(p.run("<div>", /*strict=*/false));
    }

    // ---- attributes reach the DOM, ids are queryable
    {
        P p;
        CHECK(p.run("<div id=root data-n=\"3\"><span id=inner>t</span></div>"));
        auto* root = p.doc->get_element_by_id("root");
        CHECK(root != nullptr);
        CHECK(root->get_attribute("data-n") == "3");
        CHECK(p.doc->get_element_by_id("inner") != nullptr);
    }
}
