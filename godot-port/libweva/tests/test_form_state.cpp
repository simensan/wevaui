#include "check.h"
#include "weva/dom.h"
#include "weva/form_state.h"
#include "weva_c.h"
#include <cstring>
#include <string>
#include <vector>

using namespace weva;
namespace {
struct Form {
    weva_document_t doc;
    explicit Form(const char* html) {
        weva_config c{}; c.viewport_width = 400; c.viewport_height = 300; c.use_user_agent_stylesheet = 1;
        doc = weva_document_create(&c);
        weva_document_load_html(doc, html, std::strlen(html));
        weva_document_update(doc, 0);
    }
    ~Form() { weva_document_destroy(doc); }
    weva_element_t at(const char* selector) const { return weva_document_query(doc, selector); }
    std::string value(const char* selector) const {
        char text[4096]{}; weva_element_value(doc, at(selector), text, sizeof(text)); return text;
    }
    void set(const char* selector, const char* value) { CHECK(weva_element_set_value(doc, at(selector), value) == WEVA_OK); }
    void attr(const char* selector, const char* name, const char* value) { CHECK(weva_element_set_attribute(doc, at(selector), name, value) == WEVA_OK); }
    std::string attr(const char* selector, const char* name) const {
        char text[4096]{}; weva_element_attribute(doc, at(selector), name, text, sizeof(text)); return text;
    }
    void reset() { CHECK(weva_document_reset_form(doc, at("#f")) == WEVA_OK); }
    std::vector<int> events() const {
        std::vector<int> out; weva_event e{};
        while (weva_document_poll_event(doc, &e)) out.push_back(e.kind);
        return out;
    }
};
}

void test_form_live_defaults() {
    for (const char* tag : {"input", "textarea"}) {
        auto e = make_ref<Element>(tag);
        auto text = make_ref<TextNode>("seed");
        const bool textarea = e->tag_name() == "textarea";
        if (textarea) e->append_child(text.get()); else e->set_attribute("value", "seed");
        CHECK(e->form_value() == "seed");
        int changes = 0;
        e->add_observer([&](const DomMutation& m) { if (m.kind == MutationKind::FormStateChanged) ++changes; });
        const auto version = e->form_version();
        e->set_form_value("edited"); CHECK(e->form_value() == "edited");
        CHECK(e->form_version() > version); CHECK(changes == 1);
        if (textarea) text->set_data("next"); else e->set_attribute("value", "next");
        CHECK(e->form_value() == "edited");
        e->reset_form_control(); CHECK(e->form_value() == "next");
        if (textarea) text->set_data("later"); else e->set_attribute("value", "later");
        CHECK(e->form_value() == "later");
        e->set_form_value("later");
        if (textarea) text->set_data("same value dirties"); else e->set_attribute("value", "same value dirties");
        CHECK(e->form_value() == "later");
        e->set_form_value("\r\na\rb\n"); CHECK(e->form_value() == (textarea ? "\na\nb\n" : "ab"));
        auto clone = make_ref<Element>(tag);
        clone->copy_form_state_from(*e);
        clone->set_attribute("value", "clone default");
        CHECK(clone->form_value() == e->form_value());
        e->reset_form_control(); CHECK(e->form_value() == "same value dirties");
        if (textarea) CHECK(text->data() == "same value dirties");
        e->set_form_value(std::string(512, 'a'));
        const char* stable = e->form_value().data();
        e->set_form_value(std::string(512, 'a')); CHECK(e->form_value().data() == stable);
    }
    for (const char* type : {"text", "search", "tel", "password", "url", "email", "number", "unknown", "TEXT", "range", "hidden"}) {
        auto e = make_ref<Element>("input"); e->set_attribute("type", type); e->set_attribute("value", "seed");
        e->set_form_value(" \ta\r\nb\n ");
        const std::string t(type);
        CHECK_EQ(std::string(e->form_value()), t == "number" ? "" : t == "range" ? "50" : t == "hidden" ? " \ta\r\nb\n " :
                 (t == "url" || t == "email") ? "ab" : " \tab ");
        if (t != "hidden") CHECK(e->get_attribute("value") == "seed");
    }
}

void test_abi_form_reset() {
    Form f("<form id=f on-reset=restored><input id=t value=seed><textarea id=a>seed</textarea>"
           "<input id=c type=checkbox checked><input id=r1 type=radio name=g checked><input id=r2 type=radio name=g checked>"
           "<input id=n1 type=radio checked><input id=n2 type=radio checked>"
           "<select id=s><option id=o1 selected>A</option><option id=o2 selected>B</option><option id=o3 value=''>C</option></select>"
           "<select id=list size=4><option>D</option></select><select id=multi multiple><option selected>E</option><option selected>F</option></select>"
           "<input id=range type=range value=2 step=3 max=20><button id=button type=RESET>Reset</button></form>"
           "<form id=other><input id=excluded value=other></form><input id=external form=f value=outside>");
    CHECK_EQ(f.value("#r1"), ""); CHECK_EQ(f.value("#r2"), "on");
    CHECK_EQ(f.value("#n1"), "on"); CHECK_EQ(f.value("#n2"), "on");
    CHECK_EQ(f.value("#s"), "B"); CHECK_EQ(f.value("#list"), ""); CHECK_EQ(f.value("#multi"), "E,F");
    f.set("#t", "edited"); f.set("#a", "edited"); f.set("#c", ""); f.set("#r1", "on"); f.set("#s", "");
    f.set("#external", "edited"); f.set("#excluded", "keep");
    CHECK_EQ(f.attr("#t", "value"), "seed");
    char text[64]{}; weva_element_text(f.doc, f.at("#a"), text, sizeof(text)); CHECK(std::string(text) == "seed");
    CHECK(f.at("#c[checked]") != WEVA_ELEMENT_NONE); CHECK(f.at("#c:checked") == WEVA_ELEMENT_NONE);
    CHECK(f.at("#o2[selected]") != WEVA_ELEMENT_NONE); CHECK(f.at("#o2:checked") == WEVA_ELEMENT_NONE);
    CHECK(f.at("#o3:checked") != WEVA_ELEMENT_NONE);
    CHECK_EQ(f.value("#r2"), "");
    f.attr("#t", "value", "next"); weva_element_set_text(f.doc, f.at("#a"), "next");
    CHECK_EQ(f.value("#t"), "edited"); CHECK_EQ(f.value("#a"), "edited");
    f.attr("#t", "disabled", ""); f.attr("#a", "readonly", "");
    f.events(); f.reset(); CHECK(f.events() == std::vector<int>{WEVA_EVENT_RESET});
    CHECK_EQ(f.value("#t"), "next"); CHECK_EQ(f.value("#a"), "next"); CHECK_EQ(f.value("#c"), "on");
    CHECK_EQ(f.value("#r1"), ""); CHECK_EQ(f.value("#r2"), "on"); CHECK_EQ(f.value("#s"), "B");
    CHECK_EQ(f.value("#external"), "outside"); CHECK_EQ(f.value("#excluded"), "keep");
    f.attr("#t", "value", "later"); CHECK_EQ(f.value("#t"), "later");
    f.set("#s", "missing"); CHECK_EQ(f.value("#s"), ""); CHECK(weva_document_query_all(f.doc, "#s option:checked", nullptr, 0) == 0);
    f.reset(); weva_element_remove(f.doc, f.at("#o2")); CHECK_EQ(f.value("#s"), "A");
    f.set("#range", "7"); CHECK_EQ(f.value("#range"), "8");
    f.set("#range", "10"); CHECK_EQ(f.value("#range"), "11"); CHECK_EQ(f.attr("#range", "value"), "2");
    f.reset(); CHECK_EQ(f.value("#range"), "2");
    CHECK(weva_element_form(f.doc, f.at("#external")) == f.at("#f"));
    CHECK(weva_element_form(f.doc, f.at("#excluded")) == f.at("#other"));
    f.attr("#external", "form", "missing"); f.set("#external", "keep too"); f.reset(); CHECK_EQ(f.value("#external"), "keep too");
    f.set("#c", ""); weva_document_set_focus(f.doc, f.at("#button")); f.events();
    CHECK(weva_document_key(f.doc, WEVA_KEY_ENTER, 0, 1) == 1);
    CHECK_EQ(f.value("#c"), "on");
    const auto events = f.events();
    CHECK(events == std::vector<int>({WEVA_EVENT_KEY_DOWN, WEVA_EVENT_CLICK, WEVA_EVENT_RESET}));
    CHECK(weva_document_reset_form(nullptr, 0) == WEVA_ERR_INVALID_ARGUMENT);
    CHECK(weva_document_reset_form(f.doc, f.at("#t")) == WEVA_ERR_INVALID_ARGUMENT);
}

void test_abi_reset_edit_session() {
    for (const char* html : {"<form id=f><input id=t value=seed></form><input id=outside>",
                            "<form id=f><textarea id=t>seed</textarea></form><input id=outside>"}) {
        Form f(html); const auto field = f.at("#t");
        weva_document_set_focus(f.doc, field); weva_document_text_input(f.doc, "!");
        f.events(); f.reset(); CHECK_EQ(f.value("#t"), "seed");
        CHECK(weva_document_focus(f.doc) == field); CHECK(weva_document_undo(f.doc) == 0);
        int start = -1, end = -1; weva_element_selection(f.doc, field, &start, &end); CHECK(start == 4 && end == 4);
        f.events(); weva_document_set_focus(f.doc, f.at("#outside"));
        CHECK(f.events() == std::vector<int>({WEVA_EVENT_BLUR, WEVA_EVENT_FOCUS}));
        weva_document_text_input(f.doc, "keep history");
        f.reset(); CHECK(weva_document_undo(f.doc) == 1); CHECK_EQ(f.value("#outside"), "");
        weva_document_set_focus(f.doc, field); weva_document_set_composition(f.doc, "preedit", 7, 7); f.events();
        f.reset(); CHECK_EQ(f.value("#t"), "seed");
        CHECK(weva_document_composition(f.doc, nullptr, nullptr) == WEVA_ELEMENT_NONE);
        CHECK(f.events() == std::vector<int>({WEVA_EVENT_COMPOSITION_END, WEVA_EVENT_RESET}));
        CHECK(weva_document_undo(f.doc) == 0);
        CHECK(weva_document_commit_composition(f.doc, "late") == 1);
        CHECK_EQ(f.value("#t"), "seedlate");
        CHECK(weva_document_undo(f.doc) == 1); CHECK_EQ(f.value("#t"), "seed");
        f.reset(); weva_element_set_selection(f.doc, field, 1, 2); f.reset();
        weva_element_selection(f.doc, field, &start, &end); CHECK(start == 1 && end == 2);
        f.set("#t", "programmatic"); f.events();
        weva_document_set_focus(f.doc, f.at("#outside"));
        CHECK(f.events() == std::vector<int>({WEVA_EVENT_BLUR, WEVA_EVENT_FOCUS}));
    }
}
