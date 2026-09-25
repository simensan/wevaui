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
#include <vector>

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

void test_c_abi_binding_boolean_attributes() {
    Doc doc("html,body{margin:0}button{display:block;width:120px;height:30px}",
        "<button id=action disabled='{{ Locked }}' on-click='Act'>Go</button>"
        "<button id=literal disabled='false'>Literal</button>"
        "<input id=field readonly='{{ Locked }}' required='{{ Locked }}'>"
        "<div id=aria aria-disabled='{{ Locked }}'></div>");
    const auto action = weva_document_query(doc.d, "#action");
    for (const char* value : {"false", "true", "0", "1", "", "False", "on", "false"}) {
        doc.data.values["Locked"] = value;
        doc.refresh();
        const bool locked = std::string(value) == "true" || std::string(value) == "1" || std::string(value) == "on";
        CHECK((weva_document_query(doc.d, "#action:disabled") != WEVA_ELEMENT_NONE) == locked);
        CHECK((weva_document_query(doc.d, "#field[readonly][required]") != WEVA_ELEMENT_NONE) == locked);
        CHECK(weva_document_query(doc.d, "#literal:disabled") != WEVA_ELEMENT_NONE);
        CHECK(doc.attribute("#aria", "aria-disabled") == value);
        CHECK(doc.refresh() == 0);
        weva_document_set_pointer(doc.d, 60, 15, 0);
        weva_document_set_pointer(doc.d, 60, 15, 1);
        weva_document_set_pointer(doc.d, 60, 15, 0);
        weva_event event{};
        int clicks = 0;
        while (weva_document_poll_event(doc.d, &event)) {
            if (event.kind == WEVA_EVENT_CLICK && event.target == action &&
                std::string(event.handler) == "Act") ++clicks;
        }
        CHECK(clicks == (locked ? 0 : 1));
    }
    doc.data.values.erase("Locked");
    doc.refresh();
    CHECK(weva_document_query(doc.d, "#action[disabled]") == WEVA_ELEMENT_NONE);

    Doc rows("", "<div id=list><template data-each='Items as it' data-key='Id'>"
        "<button disabled='{{ it.Locked }}'>{{ it.Id }}</button></template></div>");
    rows.data.lists["Items"] = 2;
    rows.data.values = {{"Items.0.Id", "a"}, {"Items.0.Locked", "false"},
                        {"Items.1.Id", "b"}, {"Items.1.Locked", "true"}};
    rows.refresh();
    CHECK(weva_document_query_all(rows.d, "#list > button:disabled", nullptr, 0) == 1);
    rows.data.values["Items.0.Locked"] = "true";
    rows.refresh();
    CHECK(weva_document_query_all(rows.d, "#list > button:disabled", nullptr, 0) == 2);
    rows.data.values["Items.0.Id"] = "b";
    rows.data.values["Items.1.Id"] = "a";
    rows.data.values["Items.1.Locked"] = "false";
    rows.refresh();
    CHECK(weva_document_query_all(rows.d, "#list > button:disabled", nullptr, 0) == 1);
    rows.data.lists["Items"] = 0;
    rows.refresh();
    rows.data.lists["Items"] = 2;
    rows.refresh();
    CHECK(weva_document_query_all(rows.d, "#list > button:disabled", nullptr, 0) == 1);
    const char* replacement = "<button id=new disabled='{{ Locked }}'>New</button>";
    weva_document_load_html(doc.d, replacement, std::strlen(replacement));
    doc.data.values["Locked"] = "true";
    doc.refresh();
    CHECK(weva_document_query(doc.d, "#new:disabled") != WEVA_ELEMENT_NONE);
    doc.data.values["Locked"] = "false";
    doc.refresh();
    CHECK(weva_document_query(doc.d, "#new[disabled]") == WEVA_ELEMENT_NONE);
}

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

    // Different piece boundaries may still produce exactly the same text.
    // Also cover shrinking/growing around a long unchanged literal prefix.
    Doc pieces("", "<p id=t data-caption='Northern supply cache: {{ A }}{{ B }} ready.'>Northern supply cache: {{ A }}{{ B }} ready.</p>");
    struct Parts { const char* a; const char* b; const char* joined; };
    const Parts cases[] = {{"ab","c","abc"},{"a","bc","abc"},{"","abc","abc"},
        {"abc","","abc"},{"abc","d","abcd"},{"ab","","ab"},{"","",""},
        {"longer value"," with suffix","longer value with suffix"},
        {"\xe2\x9a\x92","\xe9\x93\x81","\xe2\x9a\x92\xe9\x93\x81"}};
    std::string previous;
    for (const auto& row : cases) {
        pieces.data.values["A"] = row.a;
        pieces.data.values["B"] = row.b;
        const std::string expected = std::string("Northern supply cache: ") + row.joined + " ready.";
        CHECK(pieces.refresh() == (expected == previous ? 0 : 2));
        CHECK(pieces.text("#t") == expected);
        CHECK(pieces.attribute("#t", "data-caption") == expected);
        CHECK(pieces.refresh() == 0);
        previous = expected;
    }

    // A callback is evaluated once for every occurrence, even if all output
    // matches. Its value can change between two reads of the same path.
    Doc reads("", "<p id=t data-caption='{{ X }}/{{ X }}'>{{ X }}/{{ X }}</p>");
    int sequence = 0;
    weva_binding_source source{};
    source.user = &sequence;
    source.value = [](void* user, const char*, char* buffer, size_t capacity, int* found) -> size_t {
        const std::string value = std::to_string((*static_cast<int*>(user))++);
        *found = 1;
        if (buffer && capacity > value.size()) std::memcpy(buffer, value.c_str(), value.size()+1);
        return value.size();
    };
    weva_document_set_binding_source(reads.d, &source);
    CHECK(reads.refresh() == 2);
    CHECK(sequence == 4);
    CHECK(reads.attribute("#t", "data-caption") == "0/1");
    CHECK(reads.text("#t") == "2/3");
    sequence = 0;
    CHECK(reads.refresh() == 0);
    CHECK(sequence == 4);
    CHECK(reads.refresh() == 2);
    CHECK(sequence == 8);
    CHECK(reads.attribute("#t", "data-caption") == "4/5");
    CHECK(reads.text("#t") == "6/7");
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

    Doc selected("#b{height:10px}#b.inventory-item-selected{height:25px}",
        "<div id=b class=authored data-class-inventory-item-selected='  {{ Flag.Path }}  '></div>");
    selected.data.values["Flag.Path"] = "Player.Inventory.Selected";
    selected.data.values["Player.Inventory.Selected"] = "true";
    CHECK(selected.refresh() == 1);
    CHECK(selected.height("#b") == 25);
    CHECK(selected.attribute("#b", "class") == "authored inventory-item-selected");
    CHECK(selected.refresh() == 0);
    selected.data.values["Flag.Path"] = "Player.Inventory.AnotherSelection";
    selected.data.values["Player.Inventory.AnotherSelection"] = "false";
    CHECK(selected.refresh() == 1);
    CHECK(selected.height("#b") == 10);
    CHECK(selected.attribute("#b", "class") == "authored");
    selected.data.values.erase("Flag.Path");
    CHECK(selected.refresh() == 0);
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

// A <template>'s body is inert, as in the DOM: Chrome keeps it in
// template.content, where querySelector, textContent and children never see
// it, and nothing of it gets a box (check_template_inert_chrome.cjs). The
// core keeps the body in its tree -- data-each clones rows out of it -- so its
// readers hide it instead: the template is found, what it holds is not, bound
// or unbound.
void test_abi_template_content_is_inert() {
    Doc doc("html, body, ul, p { margin: 0; padding: 0 } li { height: 10px }",
            "<ul id=list>"
            "<template data-each='Items as item' data-key='Id'>"
            "<li class=row id='item-{{ item.Id }}'>{{ item.Name }}</li>"
            "</template>"
            "</ul><p id=after>after</p>");
    // Unbound: an empty list, not the template's body.
    CHECK(weva_document_query(doc.d, "li") == WEVA_ELEMENT_NONE);
    CHECK(weva_document_query_all(doc.d, ".row", nullptr, 0) == 0);
    CHECK(weva_document_query_all(doc.d, "#list li", nullptr, 0) == 0);
    const weva_element_t tmpl = weva_document_query(doc.d, "template");
    CHECK(tmpl != WEVA_ELEMENT_NONE);
    CHECK(weva_element_children(doc.d, tmpl, nullptr, 0) == 0);
    CHECK(doc.text("#list") == "");
    CHECK(doc.height("#list") == 0);
    double x = 0, y = 0, w = 0, h = 0;
    CHECK(weva_element_bounds(doc.d, weva_document_query(doc.d, "#after"), &x, &y, &w, &h) == WEVA_OK);
    CHECK(y == 0);

    // Bound: the rows are real, the template's own body still is not.
    doc.data.lists["Items"] = 2;
    doc.data.values["Items.0.Id"] = "a";
    doc.data.values["Items.0.Name"] = "A";
    doc.data.values["Items.1.Id"] = "b";
    doc.data.values["Items.1.Name"] = "B";
    CHECK(doc.refresh() > 0);
    CHECK(weva_document_query_all(doc.d, ".row", nullptr, 0) == 2);
    CHECK(weva_document_query_all(doc.d, "#list li", nullptr, 0) == 2);
    CHECK(weva_document_query_all(doc.d, "template li", nullptr, 0) == 0);
    CHECK(doc.attribute("li", "id") == "item-a");
    CHECK(doc.text("#list") == "AB");
    CHECK(weva_element_children(doc.d, tmpl, nullptr, 0) == 0);
    CHECK(doc.height("#list") == 20);
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

// A repeated row usually has no id -- the template writes one element and the
// data decides how many there are -- so an event from inside one used to
// arrive with nothing to say WHICH row it was.
void test_abi_row_identity() {
    Doc doc("html, body { margin: 0 } .row { height: 30px }"
            " button { display: block; width: 80px; height: 20px }",
            "<div id=list>"
            "<template data-each='Items as it' data-key='Id'>"
            "<div class=row><button on-click='Pick'>{{ it.Name }}</button></div>"
            "</template></div>");
    doc.data.lists["Items"] = 3;
    const char* names[] = {"alpha", "beta", "gamma"};
    const char* ids[] = {"a7", "b8", "c9"};
    for (int i = 0; i < 3; ++i) {
        const std::string base = "Items." + std::to_string(i);
        doc.data.values[base + ".Name"] = names[i];
        doc.data.values[base + ".Id"] = ids[i];
    }
    CHECK(doc.refresh() > 0);
    weva_document_update(doc.d, 0);

    // Every row is stamped with where it is and what it is.
    for (int i = 0; i < 3; ++i) {
        const std::string sel = "[data-weva-index='" + std::to_string(i) + "']";
        CHECK(weva_document_query(doc.d, sel.c_str()) != WEVA_ELEMENT_NONE);
    }

    int index = -1;
    char key[64] = {0};
    CHECK(weva_element_row(doc.d, weva_document_query(doc.d, "[data-weva-index='1']"), &index, key,
                           sizeof(key)) == 1);
    CHECK(index == 1);
    CHECK(std::string(key) == "b8");

    // The walk goes UP: a click lands on the button, not on the row.
    weva_element_t buttons[3] = {WEVA_ELEMENT_NONE, WEVA_ELEMENT_NONE, WEVA_ELEMENT_NONE};
    CHECK(weva_document_query_all(doc.d, "#list > .row > button", buttons, 3) == 3);
    index = -1;
    key[0] = 0;
    CHECK(weva_element_row(doc.d, buttons[2], &index, key, sizeof(key)) == 1);
    CHECK(index == 2);
    CHECK(std::string(key) == "c9");

    // An element outside any row says so rather than guessing.
    index = -1;
    key[0] = 'x';
    CHECK(weva_element_row(doc.d, weva_document_query(doc.d, "#list"), &index, key, sizeof(key)) ==
          0);
    CHECK(index == -1);
    CHECK(key[0] == 0);

    // What a handler gets: the click reports the button, and the row lookup
    // turns that into the row's own identity -- which is the whole point.
    double x = 0, y = 0, w = 0, h = 0;
    weva_element_bounds(doc.d, buttons[2], &x, &y, &w, &h);
    weva_document_set_pointer(doc.d, x + w / 2, y + h / 2, 0);
    weva_document_set_pointer(doc.d, x + w / 2, y + h / 2, 1);
    weva_document_set_pointer(doc.d, x + w / 2, y + h / 2, 0);
    weva_document_update(doc.d, 0);
    std::string handler;
    weva_element_t target = WEVA_ELEMENT_NONE;
    weva_event e{};
    while (weva_document_poll_event(doc.d, &e)) {
        if (e.kind != WEVA_EVENT_CLICK) continue;
        handler = e.handler;
        target = e.target;
    }
    CHECK(handler == "Pick");
    index = -1;
    key[0] = 0;
    CHECK(weva_element_row(doc.d, target, &index, key, sizeof(key)) == 1);
    CHECK(index == 2);
    CHECK(std::string(key) == "c9");

    // Without `data-key` the identity falls back to the position, so a row is
    // still addressable when the data carries no id of its own.
    Doc plain("html, body { margin: 0 }",
              "<div id=list><template data-each='Items as it'>"
              "<div class=row>{{ it.Name }}</div></template></div>");
    plain.data.lists["Items"] = 2;
    plain.data.values["Items.0.Name"] = "one";
    plain.data.values["Items.1.Name"] = "two";
    CHECK(plain.refresh() > 0);
    int plain_index = -1;
    char plain_key[16] = {0};
    CHECK(weva_element_row(plain.d, weva_document_query(plain.d, "[data-weva-index='1']"),
                           &plain_index, plain_key, sizeof(plain_key)) == 1);
    CHECK(plain_index == 1);
    CHECK(std::string(plain_key) == "1");
}

void test_abi_bound_dialog_open_order() {
    Doc doc("", "<dialog id=a closedby=any open='{{ A }}'>A</dialog><dialog id=b closedby=any open='{{ B }}'>B</dialog>");
    doc.data.values["A"] = "false";
    doc.data.values["B"] = "false";
    doc.refresh();
    doc.data.values["B"] = "true";
    doc.refresh();
    doc.data.values["A"] = "true";
    doc.refresh();
    const auto a = weva_document_query(doc.d, "#a");
    weva_event event{};
    while (weva_document_poll_event(doc.d, &event)) {}
    CHECK(weva_document_key(doc.d, WEVA_KEY_ESCAPE, 0, 1) == 1);
    int cancels = 0;
    while (weva_document_poll_event(doc.d, &event)) if (event.kind == WEVA_EVENT_CANCEL) {
        ++cancels;
        CHECK(event.target == a);
        CHECK(weva_document_prevent_default(doc.d));
    }
    CHECK(cancels == 1);
    doc.data.values["A"] = "false";
    doc.refresh();
    CHECK(weva_document_key(doc.d, WEVA_KEY_ESCAPE, 0, 1) == 1);
    while (weva_document_poll_event(doc.d, &event)) if (event.kind == WEVA_EVENT_CANCEL)
        CHECK(event.target == weva_document_query(doc.d, "#b"));
    CHECK(!weva_element_has_attribute(doc.d, weva_document_query(doc.d, "#b"), "open"));
}

// Getters may observe a new value when the ABI asks again with a larger buffer.
void test_abi_binding_value_changes_during_read() {
    struct ChangingData {
        std::vector<std::string> values;
        size_t calls = 0;
        bool missing_on_retry = false;
        int impossible_on_call = -1;
        size_t impossible = size_t(-1);
        static size_t read(void* user, const char*, char* buffer, size_t capacity, int* found) {
            auto& self = *static_cast<ChangingData*>(user);
            const size_t index = self.calls++;
            *found = !(self.missing_on_retry && index > 0);
            if (!*found) return 0;
            if (static_cast<int>(index) == self.impossible_on_call) return self.impossible;
            const auto& value = self.values[index < self.values.size() ? index : self.values.size() - 1];
            if (capacity) {
                const size_t n = value.size() < capacity - 1 ? value.size() : capacity - 1;
                std::memcpy(buffer, value.data(), n);
                buffer[n] = 0;
            }
            return value.size();
        }
    };
    const std::vector<std::vector<std::string>> cases = {
        {std::string(500, 'a'), "short"},
        {std::string(128, 'a'), ""},
        {std::string(128, 'a'), std::string(127, 'b')},
        {std::string(128, 'a'), std::string(500, 'b'), std::string(500, 'c')},
        {std::string(128, 'a'), std::string(129, 'b'), std::string(130, 'c')}
    };
    for (const auto& values : cases) {
        Doc doc("", "<p id=t>{{ Long }}</p>");
        ChangingData data{values};
        weva_binding_source source{};
        source.user = &data;
        source.value = &ChangingData::read;
        weva_document_set_binding_source(doc.d, &source);
        doc.refresh();
        CHECK(doc.text("#t") == values.back());
        CHECK(data.calls <= 3);
        data.calls = 0;
        data.missing_on_retry = true;
        doc.refresh();
        CHECK(doc.text("#t").empty());
        CHECK(data.calls == 2);
        data.missing_on_retry = false;
        // Beyond max_size, and merely beyond what could ever be allocated:
        // with exceptions disabled the second one aborted the host.
        for (size_t impossible : {size_t(-1), size_t(1) << 62, size_t(64) << 20}) {
            data.impossible = impossible;
            for (int invalid_call : {0, 1}) {
                data.calls = 0;
                data.impossible_on_call = invalid_call;
                doc.refresh();
                CHECK(doc.text("#t").empty());
                CHECK(data.calls == static_cast<size_t>(invalid_call + 1));
            }
        }
    }
}
