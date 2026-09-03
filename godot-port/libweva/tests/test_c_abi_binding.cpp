// `{{ path }}` in the markup, filled in from the host's data.
//
// Until this, a script could only set text and attributes one call at a time:
// it knew both what changed and where every piece of it was shown. A binding
// moves the second half into the markup, where a designer can move it around
// without the script hearing about it -- which is the whole point of writing a
// UI in HTML rather than in code.
#include "check.h"
#include "weva_c.h"

#include <cstring>
#include <map>
#include <string>

namespace {

weva_config config() {
    weva_config c{};
    c.viewport_width = 400;
    c.viewport_height = 300;
    c.use_user_agent_stylesheet = 1;
    return c;
}

// A host's data, as flat as the ABI is: paths in, text out.
struct Data {
    std::map<std::string, std::string> values;
    std::map<std::string, int> lists;

    static int length(void* user, const char* path) {
        const Data& self = *static_cast<const Data*>(user);
        const auto it = self.lists.find(path);
        return it == self.lists.end() ? -1 : it->second;
    }

    static size_t read(void* user, const char* path, char* buffer, size_t capacity, int* found) {
        const Data& self = *static_cast<const Data*>(user);
        const auto it = self.values.find(path);
        if (it == self.values.end()) {
            *found = 0;
            return 0;
        }
        *found = 1;
        const std::string& v = it->second;
        if (buffer && capacity > 0) {
            const size_t n = v.size() < capacity - 1 ? v.size() : capacity - 1;
            if (n > 0) std::memcpy(buffer, v.data(), n);
            buffer[n] = '\0';
        }
        return v.size();
    }
};

struct Doc {
    weva_document_t d = nullptr;
    Data data;

    Doc(const char* css, const char* html) {
        weva_config c = config();
        d = weva_document_create(&c);
        weva_document_add_css(d, css, std::strlen(css));
        weva_document_load_html(d, html, std::strlen(html));
        weva_binding_source source{};
        source.user = &data;
        source.value = &Data::read;
        source.count = &Data::length;
        weva_document_set_binding_source(d, &source);
        weva_document_update(d, 0);
    }
    ~Doc() { weva_document_destroy(d); }
    Doc(const Doc&) = delete;
    Doc& operator=(const Doc&) = delete;

    int refresh() {
        const int n = weva_document_refresh_bindings(d);
        weva_document_update(d, 0);
        return n;
    }
    std::string text(const char* selector) {
        // The two-call pattern, so a long value is not silently measured
        // against the test's own buffer.
        const weva_element_t e = weva_document_query(d, selector);
        const size_t n = weva_element_text(d, e, nullptr, 0);
        std::string out(n + 1, '\0');
        weva_element_text(d, e, out.data(), out.size());
        out.resize(n);
        return out;
    }
    std::string attribute(const char* selector, const char* name) {
        char buf[256] = {0};
        weva_element_attribute(d, weva_document_query(d, selector), name, buf, sizeof(buf));
        return buf;
    }
    double height(const char* selector) {
        double x = 0, y = 0, w = 0, h = 0;
        if (weva_element_bounds(d, weva_document_query(d, selector), &x, &y, &w, &h) != WEVA_OK) {
            return -1;
        }
        return h;
    }
};

}   // namespace

// Text, and the thing that makes it a binding rather than one substitution:
// the markup stays the template, so the same node fills again when the data
// moves.
void test_abi_binding_text() {
    Doc doc("html, body { margin: 0 }", "<p id=t>Gold: {{ Player.Gold }}</p>");
    doc.data.values["Player.Gold"] = "120";
    CHECK(doc.refresh() == 1);
    CHECK(doc.text("#t") == "Gold: 120");

    // Again, with a different value: the first substitution must not have
    // eaten the braces.
    doc.data.values["Player.Gold"] = "95";
    CHECK(doc.refresh() == 1);
    CHECK(doc.text("#t") == "Gold: 95");

    // A refresh that changes nothing says so, so a host can skip the work it
    // would otherwise queue.
    CHECK(doc.refresh() == 0);

    // Several in one string, and text either side of them.
    Doc many("html, body { margin: 0 }", "<p id=t>{{ A }} of {{ B }} ({{ A }})</p>");
    many.data.values["A"] = "3";
    many.data.values["B"] = "8";
    many.refresh();
    CHECK(many.text("#t") == "3 of 8 (3)");

    // A path the host does not know shows nothing rather than the path itself.
    Doc unknown("html, body { margin: 0 }", "<p id=t>[{{ Missing }}]</p>");
    unknown.refresh();
    CHECK(unknown.text("#t") == "[]");

    // An unclosed brace is text, not a swallowed line.
    Doc broken("html, body { margin: 0 }", "<p id=t>{{ half open</p>");
    broken.refresh();
    CHECK(broken.text("#t") == "{{ half open");
}

// An attribute binding, which is how a width, a title or a disabled state
// follows the data.
void test_abi_binding_attributes() {
    Doc doc("html, body { margin: 0 } .bar { height: 10px }",
            "<div id=b class=bar style='width: {{ Hp }}%'></div>");
    doc.data.values["Hp"] = "40";
    CHECK(doc.refresh() > 0);
    CHECK(doc.attribute("#b", "style") == "width: 40%");

    // And it refills, which needs the template kept: the attribute no longer
    // has braces in it after the first pass.
    doc.data.values["Hp"] = "75";
    doc.refresh();
    CHECK(doc.attribute("#b", "style") == "width: 75%");

    // The style really reached layout, not just the attribute.
    Doc sized("html, body { margin: 0 } #b { height: 10px }",
              "<div id=b style='height: {{ H }}px'></div>");
    sized.data.values["H"] = "30";
    sized.refresh();
    CHECK(sized.height("#b") == 30);
    sized.data.values["H"] = "50";
    sized.refresh();
    CHECK(sized.height("#b") == 50);
}

// `data-class-<name>` puts ONE class on or off and leaves the rest alone,
// which is what lets it live beside classes the author wrote.
void test_abi_binding_classes() {
    Doc doc("html, body { margin: 0 }",
            "<div id=b class='bar wide' data-class-hurt='Player.Hurt'"
            " data-class-low='Player.Low'></div>");
    doc.data.values["Player.Hurt"] = "True";
    doc.data.values["Player.Low"] = "False";
    doc.refresh();
    CHECK(doc.attribute("#b", "class") == "bar wide hurt");

    doc.data.values["Player.Hurt"] = "False";
    doc.data.values["Player.Low"] = "1";
    doc.refresh();
    CHECK(doc.attribute("#b", "class") == "bar wide low");

    // Off again, and what the author wrote is still there.
    doc.data.values["Player.Low"] = "0";
    doc.refresh();
    CHECK(doc.attribute("#b", "class") == "bar wide");

    // The stylesheet sees it, which is the only reason to toggle a class.
    Doc styled("html, body { margin: 0 } #b { height: 10px } #b.tall { height: 40px }",
               "<div id=b data-class-tall='Big'></div>");
    styled.data.values["Big"] = "false";
    styled.refresh();
    CHECK(styled.height("#b") == 10);
    styled.data.values["Big"] = "true";
    styled.refresh();
    CHECK(styled.height("#b") == 40);
}

// With no source, or a path-less document, nothing happens and nothing breaks.
void test_abi_binding_without_a_source() {
    weva_config c = config();
    weva_document_t d = weva_document_create(&c);
    const char* html = "<p id=t>{{ Gold }}</p>";
    weva_document_load_html(d, html, std::strlen(html));
    weva_document_update(d, 0);
    // No source set: the refresh is a no-op and the markup is left alone.
    CHECK(weva_document_refresh_bindings(d) == 0);
    char buf[64] = {0};
    weva_element_text(d, weva_document_query(d, "#t"), buf, sizeof(buf));
    CHECK(std::string(buf) == "{{ Gold }}");
    weva_document_set_binding_source(d, nullptr);
    CHECK(weva_document_refresh_bindings(d) == 0);
    weva_document_destroy(d);

    // A document with no bindings costs a walk and reports nothing.
    Doc plain("html, body { margin: 0 }", "<p id=t>Nothing to fill in</p>");
    CHECK(plain.refresh() == 0);
    CHECK(plain.text("#t") == "Nothing to fill in");
}

// A value longer than the resolver's first buffer still arrives whole: the
// two-call pattern the rest of the ABI uses.
void test_abi_binding_long_values() {
    Doc doc("html, body { margin: 0 }", "<p id=t>{{ Long }}</p>");
    doc.data.values["Long"] = std::string(500, 'x');
    doc.refresh();
    CHECK(doc.text("#t").size() == 500);
    CHECK(doc.text("#t") == std::string(500, 'x'));
}

// `data-each` makes one row per item. Until this, a list bound to game state
// had to be built by hand with append_html -- the script knowing not just what
// the data was but what shape the markup for it should take.
// The rows a repeat makes are SIBLINGS of the template, and the template keeps
// its own children as the pattern -- this parser puts a <template>'s content in
// the tree rather than in a separate fragment, which the binding layer relies
// on. So `#list > .row` is the rows and `#list .row` would also find the
// pattern inside the template.
void test_abi_binding_repeat() {
    Doc doc("html, body { margin: 0 } li { height: 10px }",
            "<ul id=list>"
            "<template data-each='Quests as quest' data-key='Id'>"
            "<li class=row>{{ $index }}. {{ quest.Title }}</li>"
            "</template>"
            "</ul>");
    doc.data.lists["Quests"] = 3;
    doc.data.values["Quests.0.Id"] = "a";
    doc.data.values["Quests.0.Title"] = "Find the key";
    doc.data.values["Quests.1.Id"] = "b";
    doc.data.values["Quests.1.Title"] = "Open the door";
    doc.data.values["Quests.2.Id"] = "c";
    doc.data.values["Quests.2.Title"] = "Leave";
    CHECK(doc.refresh() > 0);

    CHECK(weva_document_query_all(doc.d, "#list > .row", nullptr, 0) == 3);
    CHECK(doc.text("#list > .row:nth-of-type(1)") == "0. Find the key");
    CHECK(doc.text("#list > .row:nth-of-type(2)") == "1. Open the door");
    CHECK(doc.text("#list > .row:nth-of-type(3)") == "2. Leave");
    // And they are real boxes, not just DOM: the list is three rows tall.
    CHECK(doc.height("#list") == 30);

    // A VALUE changing refills in place -- the rows are the same rows, which
    // is what keeps focus and scroll inside one alive.
    doc.data.values["Quests.1.Title"] = "Open the gate";
    CHECK(doc.refresh() > 0);
    CHECK(weva_document_query_all(doc.d, "#list > .row", nullptr, 0) == 3);
    CHECK(doc.text("#list > .row:nth-of-type(2)") == "1. Open the gate");

    // The list growing adds a row.
    doc.data.lists["Quests"] = 4;
    doc.data.values["Quests.3.Id"] = "d";
    doc.data.values["Quests.3.Title"] = "Go home";
    doc.refresh();
    CHECK(weva_document_query_all(doc.d, "#list > .row", nullptr, 0) == 4);
    CHECK(doc.text("#list > .row:nth-of-type(4)") == "3. Go home");

    // And shrinking takes them away, template included -- an empty list is an
    // empty list, not the last thing it showed.
    doc.data.lists["Quests"] = 0;
    doc.refresh();
    CHECK(weva_document_query_all(doc.d, "#list > .row", nullptr, 0) == 0);
    CHECK(doc.height("#list") == 0);
}

// The rows are refilled where they stand while their keys hold, and rebuilt
// when the items themselves move. That is the whole reason `data-key` exists.
void test_abi_binding_repeat_keeps_rows() {
    Doc doc("html, body { margin: 0 }",
            "<ul id=list>"
            "<template data-each='Items as item' data-key='Id'>"
            "<li class=row>{{ item.Name }}</li>"
            "</template></ul>");
    doc.data.lists["Items"] = 2;
    doc.data.values["Items.0.Id"] = "one";
    doc.data.values["Items.0.Name"] = "First";
    doc.data.values["Items.1.Id"] = "two";
    doc.data.values["Items.1.Name"] = "Second";
    doc.refresh();

    // The rows exist and can be addressed, which is what a handle is for.
    weva_element_t rows[4] = {};
    CHECK(weva_document_query_all(doc.d, "#list > .row", rows, 4) == 2);
    const weva_element_t first = rows[0], second = rows[1];
    CHECK(first != WEVA_ELEMENT_NONE);

    // Values change, keys do not: the SAME elements are still there.
    doc.data.values["Items.0.Name"] = "Renamed";
    doc.refresh();
    weva_element_t after[4] = {};
    CHECK(weva_document_query_all(doc.d, "#list > .row", after, 4) == 2);
    CHECK(after[0] == first);
    CHECK(after[1] == second);
    CHECK(doc.text("#list > .row:nth-of-type(1)") == "Renamed");

    // A key changing is a different list, and the rows are made again.
    doc.data.values["Items.0.Id"] = "changed";
    doc.refresh();
    weva_element_t rebuilt[4] = {};
    CHECK(weva_document_query_all(doc.d, "#list > .row", rebuilt, 4) == 2);
    CHECK(rebuilt[0] != first);
}

// A repeat reads the row's own fields, the controller's, and its index, and
// `data-class-` works inside one like anywhere else.
void test_abi_binding_repeat_scope() {
    Doc doc("html, body { margin: 0 }",
            "<ul id=list>"
            "<template data-each='Items as item'>"
            "<li class=row data-class-done='item.Done'>{{ item.Name }} of {{ Total }}</li>"
            "</template></ul>");
    doc.data.lists["Items"] = 2;
    doc.data.values["Items.0.Name"] = "A";
    doc.data.values["Items.0.Done"] = "True";
    doc.data.values["Items.1.Name"] = "B";
    doc.data.values["Items.1.Done"] = "False";
    doc.data.values["Total"] = "2";
    doc.refresh();

    CHECK(doc.text("#list > .row:nth-of-type(1)") == "A of 2");   // both scopes at once
    CHECK(doc.text("#list > .row:nth-of-type(2)") == "B of 2");
    CHECK(doc.attribute("#list > .row:nth-of-type(1)", "class").find("done") != std::string::npos);
    CHECK(doc.attribute("#list > .row:nth-of-type(2)", "class").find("done") == std::string::npos);

    // Without `as`, only $index and the controller's own paths resolve.
    Doc plain("html, body { margin: 0 }",
              "<ul id=list><template data-each='Items'><li class=row>#{{ $index }}</li>"
              "</template></ul>");
    plain.data.lists["Items"] = 3;
    plain.refresh();
    CHECK(weva_document_query_all(plain.d, "#list > .row", nullptr, 0) == 3);
    CHECK(plain.text("#list > .row:nth-of-type(3)") == "#2");

    // A path that is not a list makes no rows rather than one.
    Doc scalar("html, body { margin: 0 }",
               "<ul id=list><template data-each='Nope'><li class=row>x</li></template></ul>");
    scalar.data.values["Nope"] = "not a list";
    scalar.refresh();
    CHECK(weva_document_query_all(scalar.d, "#list > .row", nullptr, 0) == 0);
}
