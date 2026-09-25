// Building a document from data: adding, replacing and removing elements.
//
// A host could set text, attributes, classes and values -- it could UPDATE a
// document, but it could not build one. An inventory, a quest log and a chat
// pane are all a list whose length is the game's business, and none of them
// can be written as markup in advance.
#include "check.h"
#include "weva_c.h"

#include <cstring>
#include <string>
#include <vector>

namespace {

weva_config config() {
    weva_config c{};
    c.viewport_width = 400;
    c.viewport_height = 300;
    c.use_user_agent_stylesheet = 1;
    return c;
}

struct Doc {
    weva_document_t d = nullptr;
    Doc(const char* css, const char* html) {
        weva_config c = config();
        d = weva_document_create(&c);
        weva_document_add_css(d, css, std::strlen(css));
        weva_document_load_html(d, html, std::strlen(html));
        weva_document_update(d, 0);
    }
    ~Doc() { weva_document_destroy(d); }
    Doc(const Doc&) = delete;
    Doc& operator=(const Doc&) = delete;

    weva_element_t at(const char* selector) { return weva_document_query(d, selector); }
    size_t count(const char* selector) { return weva_document_query_all(d, selector, nullptr, 0); }
    std::string text(weva_element_t e) {
        char buf[256] = {0};
        weva_element_text(d, e, buf, sizeof(buf));
        return buf;
    }
    double height(const char* selector) {
        double x = 0, y = 0, w = 0, h = 0;
        if (weva_element_bounds(d, at(selector), &x, &y, &w, &h) != WEVA_OK) return -1;
        return h;
    }
    weva_status append(const char* selector, const char* html, weva_element_t* out = nullptr) {
        const weva_element_t e =
            weva_element_append_html(d, at(selector), html, std::strlen(html));
        if (out) *out = e;
        return e == WEVA_ELEMENT_NONE ? WEVA_ERR_NOT_FOUND : WEVA_OK;
    }
};

const char* kCss =
    "html, body { margin: 0 }"
    "#list { width: 200px }"
    ".row { height: 20px; background: #334455 }";

}   // namespace

// Adding rows: the thing a list bound to game state does every time the state
// changes.
void test_abi_append_html() {
    Doc doc(kCss, "<div id=list></div>");
    CHECK(doc.count(".row") == 0);
    CHECK(doc.height("#list") == 0);

    weva_element_t added = WEVA_ELEMENT_NONE;
    CHECK(doc.append("#list", "<div class=row>first</div>", &added) == WEVA_OK);
    weva_document_update(doc.d, 0);
    CHECK(doc.count(".row") == 1);
    CHECK(doc.height("#list") == 20);
    // The handle comes back so a caller can fill the row it just added without
    // inventing a selector to find it again.
    CHECK(added != WEVA_ELEMENT_NONE);
    CHECK(doc.text(added) == "first");
    CHECK(weva_element_set_text(doc.d, added, "renamed") == WEVA_OK);
    weva_document_update(doc.d, 0);
    CHECK(doc.text(added) == "renamed");

    // Several at once, and the FIRST is what comes back.
    weva_element_t batch = WEVA_ELEMENT_NONE;
    doc.append("#list", "<div class=row>a</div><div class=row>b</div>", &batch);
    weva_document_update(doc.d, 0);
    CHECK(doc.count(".row") == 3);
    CHECK(doc.text(batch) == "a");
    CHECK(doc.height("#list") == 60);

    // The stylesheet applies to what was added: it was not in the document
    // when the sheet was parsed, and the cascade has to reach it anyway.
    double x = 0, y = 0, w = 0, h = 0;
    weva_element_bounds(doc.d, batch, &x, &y, &w, &h);
    CHECK(h == 20);
    CHECK(w == 200);

    // Markup with no element in it adds nothing and says so.
    weva_element_t none = WEVA_ELEMENT_NONE;
    doc.append("#list", "just text", &none);
    CHECK(none == WEVA_ELEMENT_NONE);
}

// Replacing the contents wholesale, which is what a redraw of a panel does.
void test_abi_set_html() {
    Doc doc(kCss, "<div id=list><div id=old class=row>gone</div></div>");
    const weva_element_t old = doc.at("#old");
    CHECK(old != WEVA_ELEMENT_NONE);
    CHECK(doc.height("#list") == 20);

    const char* rows = "<div class=row>x</div><div class=row>y</div>";
    CHECK(weva_element_set_html(doc.d, doc.at("#list"), rows, std::strlen(rows)) == WEVA_OK);
    weva_document_update(doc.d, 0);
    CHECK(doc.count(".row") == 2);
    CHECK(doc.height("#list") == 40);
    // What was replaced is gone: its handle stops resolving rather than
    // pointing at freed memory.
    CHECK(doc.at("#old") == WEVA_ELEMENT_NONE);
    double gone_x = 0;
    CHECK(weva_element_bounds(doc.d, old, &gone_x, nullptr, nullptr, nullptr) ==
          WEVA_ERR_NOT_FOUND);

    // And empty empties it.
    CHECK(weva_element_set_html(doc.d, doc.at("#list"), "", 0) == WEVA_OK);
    weva_document_update(doc.d, 0);
    CHECK(doc.count(".row") == 0);
    CHECK(doc.height("#list") == 0);
}

// Removing one, and the handles of everything else surviving it.
void test_abi_remove_element() {
    Doc doc(kCss,
            "<div id=list><div id=a class=row></div><div id=b class=row></div>"
            "<div id=c class=row></div></div>");
    const weva_element_t a = doc.at("#a"), b = doc.at("#b"), c = doc.at("#c");
    CHECK(doc.height("#list") == 60);

    CHECK(weva_element_remove(doc.d, b) == WEVA_OK);
    weva_document_update(doc.d, 0);
    CHECK(doc.count(".row") == 2);
    CHECK(doc.height("#list") == 40);
    CHECK(doc.at("#b") == WEVA_ELEMENT_NONE);

    // The others keep the handles they had. A handle is an index, so erasing
    // rather than tombstoning would have renumbered every element after the
    // removed one under a host still holding the old numbers.
    CHECK(doc.at("#a") == a);
    CHECK(doc.at("#c") == c);
    CHECK(doc.text(a).empty());   // resolves, rather than reading freed memory

    // Removing what is already removed is a miss, not a crash.
    CHECK(weva_element_remove(doc.d, b) == WEVA_ERR_NOT_FOUND);

    // Even the root element goes, as it does in a browser: what is left is an
    // empty document that still updates and draws nothing, rather than a
    // special case that refuses.
    CHECK(weva_element_remove(doc.d, doc.at("html")) == WEVA_OK);
    weva_document_update(doc.d, 0);
    CHECK(doc.count(".row") == 0);
    CHECK(doc.at("#list") == WEVA_ELEMENT_NONE);
    size_t draws = 0;
    weva_document_draws(doc.d, &draws);
    CHECK(draws == 0);
}

// Every match, not just the first: what a script binding a list needs to walk
// the rows it built.
void test_abi_query_all() {
    Doc doc(kCss,
            "<div id=list><div class=row>a</div><div class=row>b</div>"
            "<div class=row>c</div></div>");
    CHECK(doc.count(".row") == 3);
    CHECK(doc.count(".missing") == 0);

    weva_element_t found[8] = {};
    CHECK(weva_document_query_all(doc.d, ".row", found, 8) == 3);
    CHECK(doc.text(found[0]) == "a");
    CHECK(doc.text(found[1]) == "b");
    CHECK(doc.text(found[2]) == "c");

    // The two-call pattern: how many there ARE, however few fit.
    weva_element_t one[1] = {};
    CHECK(weva_document_query_all(doc.d, ".row", one, 1) == 3);
    CHECK(doc.text(one[0]) == "a");

    // A selector that does not parse matches nothing rather than everything.
    CHECK(weva_document_query_all(doc.d, "!!!", found, 8) == 0);
}
