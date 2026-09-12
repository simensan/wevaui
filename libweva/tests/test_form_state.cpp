#include "check.h"
#include "weva/dom.h"
#include "weva/form_state.h"
#include "weva/temporal_value.h"
#include "weva/html.h"
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

void test_number_keyboard_steps() {
    struct Case { const char* html; const char* current; bool set_current; int key;
        const char* before; const char* after; bool changed; };
    const Case cases[] = {
#include "number_step_cases.inc"
    };
    for (const auto& row : cases) {
        Form f(row.html);
        if (row.set_current) f.set("#c", row.current);
        CHECK(f.value("#c") == row.before);
        weva_document_set_focus(f.doc, f.at("#c"));
        f.events();
        weva_document_key(f.doc, row.key, 0, 1);
        CHECK(f.value("#c") == row.after);
        auto events = f.events();
        std::vector<int> values;
        for (int kind : events) if (kind == WEVA_EVENT_VALUE_CHANGED || kind == WEVA_EVENT_CHANGE) values.push_back(kind);
        CHECK(values == (row.changed ? std::vector<int>{WEVA_EVENT_VALUE_CHANGED, WEVA_EVENT_CHANGE} : std::vector<int>{}));
        weva_document_key(f.doc, row.key, 0, 0);
        CHECK(f.value("#c") == row.after);
        weva_document_set_focus(f.doc, WEVA_ELEMENT_NONE);
        for (int kind : f.events()) CHECK(kind != WEVA_EVENT_VALUE_CHANGED && kind != WEVA_EVENT_CHANGE);
        CHECK(weva_document_update(f.doc, 0) == WEVA_OK);
        CHECK(f.value("#c") == row.after);
    }
}

void test_form_live_defaults() {
    {
        Form f("<input id=c type=range min=0 max=100 step=1 value=65>");
        const auto initial = weva_element_form_version(f.doc, f.at("#c"));
        f.set("#c", "65.0");
        CHECK(weva_element_form_version(f.doc, f.at("#c")) == initial);
        f.set("#c", "66");
        const auto changed = weva_element_form_version(f.doc, f.at("#c"));
        CHECK(changed > initial);
        CHECK(weva_element_set_custom_validity(f.doc, f.at("#c"), "Reserved") == WEVA_OK);
        CHECK(weva_element_form_version(f.doc, f.at("#c")) > changed);
        CHECK(weva_element_form_version(nullptr, f.at("#c")) == 0);
        CHECK(weva_element_form_version(f.doc, WEVA_ELEMENT_NONE) == 0);
    }

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
    for (const char* setup : {"", "show", "modal", "popover"}) {
        for (const char* operation : {"show", "modal", "popover", "hide", "toggle"}) {
            Form dialog("<dialog id=d popover>Confirm</dialog>");
            const auto call = [&](const char* method) {
                const std::string m(method);
                if (m == "show" || m == "modal") return weva_element_show_dialog(dialog.doc, dialog.at("#d"), m == "modal");
                if (m == "popover") return weva_element_show_popover(dialog.doc, dialog.at("#d"));
                if (m == "hide") return weva_element_hide_popover(dialog.doc, dialog.at("#d"));
                return weva_element_toggle_popover(dialog.doc, dialog.at("#d"));
            };
            if (*setup) CHECK(call(setup) == WEVA_OK);
            dialog.events();
            const std::string s(setup), op(operation);
            bool open = s == "show" || s == "modal", modal = s == "modal", popup = s == "popover";
            bool error = false;
            if (op == "show") {
                error = open && modal;
                if (!error && !open) { open = true; popup = false; }
            } else if (op == "modal") {
                error = (open && !modal) || popup;
                if (!error) { open = true; modal = true; }
            } else if (op == "hide") popup = false;
            else {
                error = modal && !popup;
                if (!error) popup = op == "toggle" ? !popup : true;
            }
            CHECK(call(operation) == (error ? WEVA_ERR_INVALID_STATE : WEVA_OK));
            CHECK((dialog.at("#d[open]") != WEVA_ELEMENT_NONE) == open);
            CHECK((dialog.at("#d:modal") != WEVA_ELEMENT_NONE) == modal);
            CHECK((dialog.at("#d:popover-open") != WEVA_ELEMENT_NONE) == popup);
            if (error) CHECK(dialog.events().empty());
        }
    }
    for (const char* mode : {"auto", "manual", "hint"}) for (bool nested : {false, true}) {
        const std::string html = std::string("<div id=p popover=") + mode + ">" +
            (nested ? "<dialog id=d>Confirm</dialog></div>" : "Menu</div><dialog id=d>Confirm</dialog>");
        Form dialog(html.c_str());
        weva_element_show_popover(dialog.doc, dialog.at("#p"));
        dialog.events();
        weva_element_show_dialog(dialog.doc, dialog.at("#d"), 1);
        CHECK((dialog.at("#p:popover-open") != WEVA_ELEMENT_NONE) == (nested || std::string(mode) == "manual"));
        std::vector<std::string> states;
        weva_event event{};
        while (weva_document_poll_event(dialog.doc, &event)) if (event.kind == WEVA_EVENT_TOGGLE) states.emplace_back(event.text);
        CHECK(states == (nested || std::string(mode) == "manual" ? std::vector<std::string>{"open"} : std::vector<std::string>{"closed", "open"}));
        CHECK(weva_element_show_dialog(dialog.doc, dialog.at("#d"), 1) == WEVA_OK);
        CHECK(dialog.events().empty());
    }
    for (const char* next_mode : {static_cast<const char*>(nullptr), "manual", "hint", "invalid", "AUTO"}) {
        Form popup("<div id=p popover on-toggle=changed>Menu</div>");
        weva_element_show_popover(popup.doc, popup.at("#p"));
        popup.events();
        popup.attr("#p", "popover", next_mode);
        const bool closes = !next_mode || std::string(next_mode) != "AUTO";
        weva_event event{};
        int toggles = 0;
        while (weva_document_poll_event(popup.doc, &event)) {
            if (event.kind != WEVA_EVENT_TOGGLE) continue;
            ++toggles;
            CHECK(event.target == popup.at("#p"));
            CHECK(std::string(event.handler) == "changed");
            CHECK(std::string(event.text) == "closed");
        }
        CHECK(toggles == (closes ? 1 : 0));
        CHECK((popup.at("#p:popover-open") == WEVA_ELEMENT_NONE) == closes);
        if (!next_mode) popup.attr("#p", "popover", "");
        weva_element_show_popover(popup.doc, popup.at("#p"));
        popup.events();
        weva_element_hide_popover(popup.doc, popup.at("#p"));
        weva_element_hide_popover(popup.doc, popup.at("#p"));
        const auto events = popup.events();
        CHECK(std::count(events.begin(), events.end(), WEVA_EVENT_TOGGLE) == 1);
    }
    {
        Form popup("<div id=p popover>Menu</div>");
        weva_element_show_popover(popup.doc, popup.at("#p"));
        weva_element_hide_popover(popup.doc, popup.at("#p"));
        std::vector<std::string> states;
        weva_event event{};
        while (weva_document_poll_event(popup.doc, &event))
            if (event.kind == WEVA_EVENT_TOGGLE) states.emplace_back(event.text);
        CHECK(states == (std::vector<std::string>{"open", "closed"}));
    }
    {
        Form top("<dialog id=d data-modal><span id=label>Confirm</span></dialog>"
                 "<div id=p popover data-popover-open><span id=item>Menu</span></div>");
        const char* css = "dialog:modal span{width:37px}dialog:not(:modal) span{width:73px}"
                          ":popover-open span{width:41px}[popover]:not(:popover-open) span{width:79px}";
        weva_document_add_css(top.doc, css, std::strlen(css));
        const auto width = [&](const char* id) {
            weva_document_update(top.doc, 0);
            char value[32]{}; weva_element_computed_style(top.doc, top.at(id), "width", value, sizeof(value));
            return std::string(value);
        };
        CHECK(top.at("#d:modal") == WEVA_ELEMENT_NONE);
        CHECK(top.at("#p:popover-open") == WEVA_ELEMENT_NONE);
        CHECK(weva_element_show_dialog(top.doc, top.at("#d"), 0) == WEVA_OK);
        CHECK(width("#label") == "73px");
        weva_element_close_dialog(top.doc, top.at("#d"));
        weva_element_show_dialog(top.doc, top.at("#d"), 1);
        CHECK(width("#label") == "37px");
        top.attr("#d", "data-modal", nullptr);
        CHECK(top.at("#d:modal") != WEVA_ELEMENT_NONE);
        top.attr("#d", "open", nullptr);
        CHECK(top.at("#d:modal") != WEVA_ELEMENT_NONE);
        top.attr("#d", "open", "");
        CHECK(width("#label") == "37px");
        weva_element_close_dialog(top.doc, top.at("#d"));
        weva_element_show_popover(top.doc, top.at("#p"));
        CHECK(width("#item") == "41px");
        top.attr("#p", "popover", "AUTO");
        CHECK(top.at("#p:popover-open") != WEVA_ELEMENT_NONE);
        top.attr("#p", "data-popover-open", nullptr);
        CHECK(top.at("#p:popover-open") != WEVA_ELEMENT_NONE);
        weva_document_key(top.doc, WEVA_KEY_ESCAPE, 0, 1);
        weva_document_key(top.doc, WEVA_KEY_ESCAPE, 0, 0);
        CHECK(width("#item") == "79px");
        weva_element_show_popover(top.doc, top.at("#p"));
        top.attr("#p", "popover", nullptr);
        CHECK(top.at("#p:popover-open") == WEVA_ELEMENT_NONE);
        top.attr("#p", "popover", "");
        CHECK(width("#item") == "79px");
        weva_element_show_popover(top.doc, top.at("#p"));
        CHECK(width("#item") == "41px");
        weva_element_hide_popover(top.doc, top.at("#p"));
        CHECK(width("#item") == "79px");
        for (const char* mode : {"MANUAL", "invalid"}) {
            top.attr("#p", "popover", mode);
            weva_element_show_popover(top.doc, top.at("#p"));
            weva_document_key(top.doc, WEVA_KEY_ESCAPE, 0, 1);
            weva_document_key(top.doc, WEVA_KEY_ESCAPE, 0, 0);
            CHECK(top.at("#p:popover-open") != WEVA_ELEMENT_NONE);
            weva_element_hide_popover(top.doc, top.at("#p"));
        }
    }
    for (const char* attributes : {"command='--open'", "commandfor=missing", "command='' commandfor=''", "type=invalid command='--open'"}) {
        const std::string html = std::string("<form id=f><input id=field><button id=command ") + attributes +
            ">Inspect</button><button id=apply>Apply</button></form>";
        Form form(html.c_str());
        const auto enter = [&](const char* selector) {
            CHECK(weva_document_set_focus(form.doc, form.at(selector)) == WEVA_OK);
            form.events();
            weva_document_key(form.doc, WEVA_KEY_ENTER, 0, 1);
            weva_document_key(form.doc, WEVA_KEY_ENTER, 0, 0);
        };
        enter("#command");
        auto events = form.events();
        CHECK(std::count(events.begin(), events.end(), WEVA_EVENT_CLICK) == 1);
        CHECK(std::count(events.begin(), events.end(), WEVA_EVENT_SUBMIT) == 0);
        enter("#field");
        weva_event event{};
        int clicks = 0, submits = 0;
        while (weva_document_poll_event(form.doc, &event)) {
            if (event.kind == WEVA_EVENT_CLICK) { ++clicks; CHECK(event.target == form.at("#apply")); }
            if (event.kind == WEVA_EVENT_SUBMIT) ++submits;
        }
        CHECK(clicks == 1 && submits == 1);
        form.attr("#apply", "disabled", "");
        enter("#field");
        events = form.events();
        CHECK(std::count(events.begin(), events.end(), WEVA_EVENT_CLICK) == 0);
        CHECK(std::count(events.begin(), events.end(), WEVA_EVENT_SUBMIT) == 0);
        form.attr("#command", "type", "SUBMIT");
        enter("#command");
        events = form.events();
        CHECK(std::count(events.begin(), events.end(), WEVA_EVENT_SUBMIT) == 1);
    }
    {
        Form defaults("<input id=external type=submit form=f disabled><form id=f>"
                      "<button id=auto><span id=label>Apply</span></button><input id=image type=image>"
                      "<button id=reset type=reset>Reset</button><input id=check type=checkbox checked>"
                      "<select id=select><option id=a selected>A</option><option id=b>B</option></select></form>"
                      "<button id=orphan>Outside</button>");
        CHECK(defaults.at("#external:default") != WEVA_ELEMENT_NONE);
        for (const char* id : {"#auto:default", "#image:default", "#reset:default", "#orphan:default"})
            CHECK(defaults.at(id) == WEVA_ELEMENT_NONE);
        CHECK(defaults.at("#check:default") != WEVA_ELEMENT_NONE);
        CHECK(defaults.at("#a:default") != WEVA_ELEMENT_NONE);
        defaults.set("#check", "false"); defaults.set("#select", "B");
        CHECK(defaults.at("#check:default") != WEVA_ELEMENT_NONE);
        CHECK(defaults.at("#a:default") != WEVA_ELEMENT_NONE);
        CHECK(defaults.at("#b:default") == WEVA_ELEMENT_NONE);
        const char* css = "button:default span{width:37px}button:not(:default) span{width:73px}";
        weva_document_add_css(defaults.doc, css, std::strlen(css));
        const auto width = [&]() {
            weva_document_update(defaults.doc, 0);
            char value[32]{};
            weva_element_computed_style(defaults.doc, defaults.at("#label"), "width", value, sizeof(value));
            return std::string(value);
        };
        CHECK(width() == "73px");
        defaults.attr("#external", "form", "missing");
        CHECK(width() == "37px");
        defaults.attr("#external", "form", "f");
        CHECK(width() == "73px");
        defaults.attr("#external", "type", "button");
        CHECK(width() == "37px");
        defaults.attr("#auto", "command", "toggle-popover");
        CHECK(width() == "73px");
        CHECK(defaults.at("#image:default") != WEVA_ELEMENT_NONE);
        defaults.attr("#auto", "type", "SUBMIT");
        CHECK(width() == "37px");
        defaults.attr("#f", "id", "renamed");
        CHECK(width() == "37px");
        defaults.attr("#external", "type", "image");
        defaults.attr("#external", "form", "renamed");
        CHECK(width() == "73px");
        defaults.attr("#check", "checked", nullptr);
        CHECK(defaults.at("#check:default") == WEVA_ELEMENT_NONE);
        defaults.attr("#a", "selected", nullptr);
        CHECK(defaults.at("#a:default") == WEVA_ELEMENT_NONE);
        CHECK(weva_element_remove(defaults.doc, defaults.at("#external")) == WEVA_OK);
        CHECK(width() == "37px");
        CHECK(weva_element_remove(defaults.doc, defaults.at("#auto")) == WEVA_OK);
        weva_document_update(defaults.doc, 0);
        CHECK(defaults.at("#image:default") != WEVA_ELEMENT_NONE);
    }
    for (const char* type : {"text", "search", "tel", "url", "email", "password", "date", "month",
                            "week", "time", "datetime-local", "number", "checkbox", "radio", "file",
                            "hidden", "range", "color", "button", "submit", "reset", "image", "unknown"}) {
        const std::string html = std::string("<input id=field type='") + type + "'>";
        Form field(html.c_str());
        const std::string t(type);
        const bool editable = t != "checkbox" && t != "radio" && t != "file" && t != "hidden" &&
                              t != "range" && t != "color" && t != "button" && t != "submit" &&
                              t != "reset" && t != "image";
        for (const char* attribute : {"", "readonly", "disabled"}) {
            field.attr("#field", "readonly", nullptr);
            field.attr("#field", "disabled", nullptr);
            if (*attribute) field.attr("#field", attribute, "false");
            const bool expected = editable && !*attribute;
            CHECK((field.at("#field:read-write") != WEVA_ELEMENT_NONE) == expected);
            CHECK((field.at("#field:read-only") != WEVA_ELEMENT_NONE) == !expected);
        }
    }
    {
        Form field("<div id=root contenteditable><span id=child>Text</span>"
                   "<span id=off contenteditable=false><i id=on contenteditable=TRUE>On</i></span>"
                   "<span id=invalid contenteditable=invalid>Inherited</span>"
                   "<span id=space contenteditable=' true '>Inherited</span>"
                   "<span id=plain contenteditable=plaintext-only>Plain</span>"
                   "<input id=input readonly contenteditable><input id=check type=checkbox contenteditable>"
                   "<button id=button disabled>Button</button><textarea id=area disabled contenteditable></textarea>"
                   "<select id=select disabled><option id=option>A</option></select></div><div id=ordinary>Text</div>");
        for (const char* id : {"#root", "#child", "#on", "#invalid", "#space", "#plain", "#button", "#select", "#option"})
            CHECK(field.at((std::string(id) + ":read-write").c_str()) != WEVA_ELEMENT_NONE);
        for (const char* id : {"#off", "#input", "#check", "#area", "#ordinary"})
            CHECK(field.at((std::string(id) + ":read-only").c_str()) != WEVA_ELEMENT_NONE);
        const char* css = "#child:read-write{width:37px}#child:read-only{width:73px}";
        weva_document_add_css(field.doc, css, std::strlen(css));
        for (const char* value : {"true", "false", "invalid", "PLAINTEXT-ONLY"}) {
            field.attr("#root", "contenteditable", value);
            weva_document_update(field.doc, 0);
            char width[32]{};
            weva_element_computed_style(field.doc, field.at("#child"), "width", width, sizeof(width));
            const bool editable = std::string(value) == "true" || std::string(value) == "PLAINTEXT-ONLY";
            CHECK(std::string(width) == (editable ? "37px" : "73px"));
        }
    }
    {
        Form field("<fieldset id=group disabled><legend><input id=master></legend>"
                   "<input id=field><textarea id=area></textarea></fieldset>");
        CHECK(field.at("#master:read-write") != WEVA_ELEMENT_NONE);
        CHECK(field.at("#field:read-only") != WEVA_ELEMENT_NONE);
        CHECK(field.at("#area:read-only") != WEVA_ELEMENT_NONE);
        field.attr("#group", "disabled", nullptr);
        CHECK(field.at("#field:read-write") != WEVA_ELEMENT_NONE);
        CHECK(field.at("#area:read-write") != WEVA_ELEMENT_NONE);
        const char* css = "#field:read-write{width:37px}#field:read-only{width:73px}";
        weva_document_add_css(field.doc, css, std::strlen(css));
        for (const char* type : {"text", "checkbox", "TEXT", "range", "unknown"}) {
            field.attr("#field", "type", type);
            weva_document_update(field.doc, 0);
            char width[32]{};
            weva_element_computed_style(field.doc, field.at("#field"), "width", width, sizeof(width));
            const bool editable = std::string(type) != "checkbox" && std::string(type) != "range";
            CHECK(std::string(width) == (editable ? "37px" : "73px"));
        }
        field.attr("#area", "readonly", "false");
        CHECK(field.at("#area:read-only") != WEVA_ELEMENT_NONE);
        field.attr("#area", "readonly", nullptr);
        CHECK(field.at("#area:read-write") != WEVA_ELEMENT_NONE);
    }
    for (const char* type : {"text", "search", "tel", "url", "email", "password", "date", "month",
                            "week", "time", "datetime-local", "number", "checkbox", "radio", "file",
                            "hidden", "range", "color", "button", "submit", "reset", "image", "unknown"}) {
        const std::string html = std::string("<input id=field type='") + type + "' required disabled readonly>";
        Form required(html.c_str());
        const std::string t(type);
        const bool expected = t != "hidden" && t != "range" && t != "color" && t != "button" &&
                              t != "submit" && t != "reset" && t != "image";
        CHECK((required.at("#field:required") != WEVA_ELEMENT_NONE) == expected);
        CHECK((required.at("#field:optional") != WEVA_ELEMENT_NONE) == !expected);
        required.attr("#field", "required", nullptr);
        CHECK(required.at("#field:required") == WEVA_ELEMENT_NONE);
        CHECK(required.at("#field:optional") != WEVA_ELEMENT_NONE);
    }
    {
        Form required("<textarea id=a required></textarea><select id=s required><option>A</option></select>"
                      "<button id=b required>Go</button><div id=d required>Text</div><input id=i>");
        CHECK(required.at("#a:required") != WEVA_ELEMENT_NONE);
        CHECK(required.at("#s:required") != WEVA_ELEMENT_NONE);
        CHECK(required.at("#b:optional") != WEVA_ELEMENT_NONE);
        CHECK(required.at("#d:required") == WEVA_ELEMENT_NONE);
        const char* css = "input:required{width:37px}input:optional{width:73px}";
        weva_document_add_css(required.doc, css, std::strlen(css));
        for (const char* value : {"", static_cast<const char*>(nullptr), "false"}) {
            required.attr("#i", "required", value);
            weva_document_update(required.doc, 0);
            char width[32]{};
            weva_element_computed_style(required.doc, required.at("#i"), "width", width, sizeof(width));
            CHECK(std::string(width) == (value ? "37px" : "73px"));
        }
    }
    {
        Form group("<fieldset id=group disabled><div id=ordinary>Text</div>"
            "<legend><input id=master><input id=own disabled></legend>"
            "<input id=blocked><legend><input id=second></legend>"
            "<fieldset><legend><input id=nested></legend></fieldset>"
            "<select><optgroup disabled><option id=option>A</option></optgroup></select>"
            "</fieldset>");
        const char* css = "input:disabled{width:37px}input:enabled{width:73px}";
        weva_document_add_css(group.doc, css, std::strlen(css));
        weva_document_update(group.doc, 0);
        char width[32]{};
        weva_element_computed_style(group.doc, group.at("#blocked"), "width", width, sizeof(width));
        CHECK(std::string(width) == "37px");
        CHECK(group.at("#master:enabled") != WEVA_ELEMENT_NONE);
        for (const char* selector : {"#own:disabled", "#blocked:disabled", "#second:disabled", "#nested:disabled", "#option:disabled"})
            CHECK(group.at(selector) != WEVA_ELEMENT_NONE);
        CHECK(group.at("#ordinary:enabled") == WEVA_ELEMENT_NONE);
        CHECK(group.at("#ordinary:disabled") == WEVA_ELEMENT_NONE);
        weva_document_set_focus(group.doc, group.at("#master"));
        CHECK(weva_document_try_text_input(group.doc, "on") == 1);
        CHECK(group.value("#master") == "on");
        weva_document_set_focus(group.doc, WEVA_ELEMENT_NONE);
        weva_document_set_focus(group.doc, group.at("#blocked"));
        CHECK(weva_document_try_text_input(group.doc, "no") == 0);
        CHECK(group.value("#blocked").empty());
        group.attr("#group", "disabled", nullptr);
        weva_document_update(group.doc, 0);
        CHECK(group.at("#blocked:enabled") != WEVA_ELEMENT_NONE);
        CHECK(group.at("#second:enabled") != WEVA_ELEMENT_NONE);
        CHECK(group.at("#nested:enabled") != WEVA_ELEMENT_NONE);
        CHECK(group.at("#own:disabled") != WEVA_ELEMENT_NONE);
        weva_element_computed_style(group.doc, group.at("#blocked"), "width", width, sizeof(width));
        CHECK(std::string(width) == "73px");
        weva_document_set_focus(group.doc, group.at("#blocked"));
        group.attr("#group", "disabled", "");
        weva_document_update(group.doc, 0);
        CHECK(weva_document_focus(group.doc) == WEVA_ELEMENT_NONE);
        weva_document_set_focus(group.doc, group.at("#master"));
        group.attr("#group", "disabled", "");
        weva_document_update(group.doc, 0);
        CHECK(weva_document_focus(group.doc) == group.at("#master"));
    }
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

void test_form_required_validation() {
    struct Case { const char* html; bool candidate; bool missing; };
    const Case cases[] = {
        {"<form><input id=c required></form>", true, true},
        {"<form><input id=c required value=ok></form>", true, false},
        {"<form><input id=c required value=\" \"></form>", true, false},
        {"<form><input id=c readonly required></form>", false, false},
        {"<form><input id=c disabled required></form>", false, false},
        {"<form><fieldset disabled><input id=c required></fieldset></form>", false, false},
        {"<form><fieldset disabled><legend><input id=c required></legend></fieldset></form>", true, true},
        {"<form><datalist><input id=c required></datalist></form>", false, true},
        {"<form><input id=c type=hidden required></form>", false, false},
        {"<form><input id=c type=checkbox required></form>", true, true},
        {"<form><input id=c type=checkbox readonly required></form>", false, true},
        {"<form><input id=c type=checkbox checked required></form>", true, false},
        {"<form><input id=c type=checkbox disabled required></form>", false, true},
        {"<form><input id=c type=radio disabled required></form>", false, false},
        {"<form><input id=c type=radio required></form>", true, false},
        {"<form><select id=c disabled required><option value=\"\">Choose</option></select></form>", false, true},
        {"<form><input id=c type=radio name=g><input type=radio name=g required></form>", true, true},
        {"<form><input id=c type=radio name=g><input type=radio name=g required checked></form>", true, false},
        {"<form><input id=c type=radio name=g><input type=radio name=g disabled required></form>", true, true},
        {"<form><input id=c type=radio name=g required><input type=radio name=g disabled checked></form>", true, false},
        {"<form><input id=c type=radio required><input type=radio checked></form>", true, false},
        {"<form><textarea id=c required></textarea></form>", true, true},
        {"<form><textarea id=c readonly required></textarea></form>", false, false},
        {"<form><select id=c required><option value=\"\">Choose</option><option>Yes</option></select></form>", true, true},
        {"<form><select id=c required><option>Yes</option><option value=\"\" selected>Empty</option></select></form>", true, false},
        {"<form><select id=c required><optgroup label=Group><option value=\"\">Empty</option></optgroup></select></form>", true, false},
        {"<form><select id=c required multiple><option value=\"\" selected>Empty</option></select></form>", true, false},
        {"<form><select id=c required size=2><option value=\"\" selected>Empty</option></select></form>", true, false},
        {"<form><select id=c required multiple><option>Yes</option></select></form>", true, true},
        {"<form><select id=c required><option value=yes selected disabled>Yes</option></select></form>", true, false},
    };
    for (const auto& row : cases) {
        SymbolTable symbols;
        HtmlParseError error{};
        auto document = parse_html(row.html, &symbols, ParseOptions{}, &error);
        CHECK(bool(document));
        auto* control = document->get_element_by_id("c");
        CHECK(control);
        CHECK(form_is_validation_candidate(*control) == row.candidate);
        CHECK(form_required_value_missing(*control) == row.missing);
    }
}

void test_form_custom_validity() {
    for (const char* tag : {"input", "textarea", "select", "button"}) {
        auto control = make_ref<Element>(tag);
        const auto initial = control->form_version();
        control->set_custom_validity("");
        CHECK(control->form_version() == initial);
        control->set_custom_validity("Reserved name");
        const auto changed = control->form_version();
        CHECK(changed > initial);
        control->set_custom_validity("Reserved name");
        CHECK(control->form_version() == changed);
        control->reset_form_control();
        CHECK(control->custom_validity() == "Reserved name");
        auto clone = make_ref<Element>(tag);
        clone->copy_form_state_from(*control);
        CHECK(clone->custom_validity().empty());
        CHECK(control->custom_validity() == "Reserved name");
        control->set_custom_validity("");
        CHECK(control->custom_validity().empty());
    }
}

void test_form_number_validation() {
    struct Case { const char *min, *max, *step, *initial, *value, *sanitized; bool underflow, overflow, mismatch; };
    const Case cases[] = {
        {nullptr, nullptr, nullptr, nullptr, "", "", false, false, false},
        {nullptr, nullptr, nullptr, nullptr, "1", "1", false, false, false},
        {nullptr, nullptr, nullptr, nullptr, "1.5", "1.5", false, false, true},
        {"3", nullptr, nullptr, nullptr, "2", "2", true, false, false},
        {nullptr, "3", nullptr, nullptr, "4", "4", false, true, false},
        {"3", "1", nullptr, nullptr, "2", "2", true, true, false},
        {"0", nullptr, "2", nullptr, "3", "3", false, false, true},
        {"0", nullptr, "2", nullptr, "4", "4", false, false, false},
        {nullptr, nullptr, "any", nullptr, "1.5", "1.5", false, false, false},
        {nullptr, nullptr, "AnY", nullptr, "1.5", "1.5", false, false, false},
        {nullptr, nullptr, "0", nullptr, "1.5", "1.5", false, false, true},
        {nullptr, nullptr, "-2", nullptr, "1.5", "1.5", false, false, true},
        {nullptr, nullptr, "bad", nullptr, "1.5", "1.5", false, false, true},
        {nullptr, nullptr, "0.1", nullptr, "0.3", "0.3", false, false, false},
        {"0.2", nullptr, "0.3", nullptr, "0.5", "0.5", false, false, false},
        {nullptr, nullptr, "0.3", "0.2", "0.5", "0.5", false, false, false},
        {nullptr, nullptr, "0.3", "0.2", "0.6", "0.6", false, false, true},
        {"0", nullptr, "0.3", "0.2", "0.6", "0.6", false, false, false},
        {"bad", nullptr, "0.3", "0.2", "0.5", "0.5", false, false, false},
        {" 3", nullptr, nullptr, nullptr, "2", "2", false, false, false},
        {"+3", nullptr, nullptr, nullptr, "2", "2", false, false, false},
        {nullptr, "3 ", nullptr, nullptr, "4", "4", false, false, false},
        {"3x", nullptr, nullptr, nullptr, "2", "2", false, false, false},
        {nullptr, nullptr, "1", nullptr, "1.00000001", "1.00000001", false, false, false},
        {nullptr, nullptr, "1", nullptr, "1.0000001", "1.0000001", false, false, true},
        {nullptr, nullptr, "0.1", nullptr, "0.300000001", "0.300000001", false, false, false},
        {nullptr, nullptr, "0.1", nullptr, "0.30000001", "0.30000001", false, false, true},
        {"-3", nullptr, "2", nullptr, "-1", "-1", false, false, false},
        {nullptr, nullptr, "0.01", nullptr, "1000000000000.03", "1000000000000.03", false, false, false},
        {nullptr, nullptr, "0.01", nullptr, "1000000000000.031", "1000000000000.031", false, false, true},
        {nullptr, nullptr, "1e-20", nullptr, "0.1", "0.1", false, false, false},
        {nullptr, nullptr, "1e-308", nullptr, "1", "1", false, false, false},
        {nullptr, nullptr, "1e308", nullptr, "1e308", "1e308", false, false, false},
        {"-1e308", nullptr, "1e308", nullptr, "1e308", "1e308", false, false, false},
        {"1e308", "1e308", nullptr, nullptr, "1e308", "1e308", false, false, false},
        {nullptr, nullptr, "1e999", nullptr, "1.5", "1.5", false, false, true},
        {nullptr, nullptr, "1e-999", nullptr, "1.5", "1.5", false, false, false},
        {nullptr, nullptr, "1.", nullptr, "1.5", "1.5", false, false, true},
        {"1000000000000.031", nullptr, nullptr, nullptr, "1000000000000.03099", "1000000000000.03099", true, false, true},
        {nullptr, "1000000000000.031", nullptr, nullptr, "1000000000000.03101", "1000000000000.03101", false, true, true},
        {"1.000000000000000001", nullptr, nullptr, nullptr, "1", "1", false, false, false},
        {nullptr, "1", nullptr, nullptr, "1.000000000000000001", "1.000000000000000001", false, false, false},
        {nullptr, nullptr, nullptr, nullptr, "1.e2", "1.e2", false, false, false},
        {nullptr, nullptr, nullptr, nullptr, "1e-999", "1e-999", false, false, false},
        {nullptr, nullptr, nullptr, nullptr, ".5", ".5", false, false, true},
        {nullptr, nullptr, nullptr, nullptr, "-0", "-0", false, false, false},
        {"1.e2", nullptr, nullptr, nullptr, "1", "1", true, false, false},
        {"-.5", nullptr, nullptr, nullptr, "-.6", "-.6", true, false, true},
        {nullptr, "2e2", nullptr, nullptr, "201", "201", false, true, false},
    };
    for (const auto& row : cases) {
        auto e = make_ref<Element>("input");
        e->set_attribute("type", "number");
        if (row.min) e->set_attribute("min", row.min);
        if (row.max) e->set_attribute("max", row.max);
        if (row.step) e->set_attribute("step", row.step);
        if (row.initial) e->set_attribute("value", row.initial);
        e->set_form_value(row.value);
        CHECK(e->form_value() == row.sanitized);
        const auto validity = form_number_validity(*e);
        CHECK(validity.range_underflow == row.underflow);
        CHECK(validity.range_overflow == row.overflow);
        CHECK(validity.step_mismatch == row.mismatch);
    }
    // Chrome differential cases: exponent saturation must not bypass trailing syntax.
    struct ExponentCase { std::string raw; const char *attribute, *sanitized; bool underflow, overflow, mismatch; };
    const ExponentCase exponents[] = {
        {std::string("0e9999", 6), "current", "0e9999", false, false, false},
        {std::string("0e9999", 6), "value", "0e9999", false, false, false},
        {std::string("0e9999", 6), "min", "-1", true, false, false},
        {std::string("0e9999", 6), "max", "1.5", false, true, true},
        {std::string("0e9999", 6), "step", "1.5", false, false, true},
        {std::string("0e9999junk", 10), "current", "", false, false, false},
        {std::string("0e9999junk", 10), "value", "", false, false, false},
        {std::string("0e9999junk", 10), "min", "-1", true, false, false},
        {std::string("0e9999junk", 10), "max", "1.5", false, true, true},
        {std::string("0e9999junk", 10), "step", "1.5", false, false, true},
        {std::string("1e-9999", 7), "current", "1e-9999", false, false, false},
        {std::string("1e-9999", 7), "value", "1e-9999", false, false, false},
        {std::string("1e-9999", 7), "min", "-1", true, false, false},
        {std::string("1e-9999", 7), "max", "1.5", false, true, true},
        {std::string("1e-9999", 7), "step", "1.5", false, false, true},
        {std::string("1e-9999junk", 11), "current", "", false, false, false},
        {std::string("1e-9999junk", 11), "value", "", false, false, false},
        {std::string("1e-9999junk", 11), "min", "-1", true, false, false},
        {std::string("1e-9999junk", 11), "max", "1.5", false, true, true},
        {std::string("1e-9999junk", 11), "step", "1.5", false, false, true},
        {std::string("-0e9999x", 8), "current", "", false, false, false},
        {std::string("-0e9999x", 8), "value", "", false, false, false},
        {std::string("-0e9999x", 8), "min", "-1", true, false, false},
        {std::string("-0e9999x", 8), "max", "1.5", false, true, true},
        {std::string("-0e9999x", 8), "step", "1.5", false, false, true},
        {std::string("1e9999x", 7), "current", "", false, false, false},
        {std::string("1e9999x", 7), "value", "", false, false, false},
        {std::string("1e9999x", 7), "min", "-1", false, false, false},
        {std::string("1e9999x", 7), "max", "1.5", false, false, true},
        {std::string("1e9999x", 7), "step", "1.5", false, false, true},
        {std::string("0e9999 ", 7), "current", "", false, false, false},
        {std::string("0e9999 ", 7), "value", "", false, false, false},
        {std::string("0e9999 ", 7), "min", "-1", true, false, false},
        {std::string("0e9999 ", 7), "max", "1.5", false, true, true},
        {std::string("0e9999 ", 7), "step", "1.5", false, false, true},
        {std::string("0e9999+", 7), "current", "", false, false, false},
        {std::string("0e9999+", 7), "value", "", false, false, false},
        {std::string("0e9999+", 7), "min", "-1", true, false, false},
        {std::string("0e9999+", 7), "max", "1.5", false, true, true},
        {std::string("0e9999+", 7), "step", "1.5", false, false, true},
        {std::string("0e9999.0", 8), "current", "", false, false, false},
        {std::string("0e9999.0", 8), "value", "", false, false, false},
        {std::string("0e9999.0", 8), "min", "-1", true, false, false},
        {std::string("0e9999.0", 8), "max", "1.5", false, true, true},
        {std::string("0e9999.0", 8), "step", "1.5", false, false, true},
        {std::string("0e9999e1", 8), "current", "", false, false, false},
        {std::string("0e9999e1", 8), "value", "", false, false, false},
        {std::string("0e9999e1", 8), "min", "-1", true, false, false},
        {std::string("0e9999e1", 8), "max", "1.5", false, true, true},
        {std::string("0e9999e1", 8), "step", "1.5", false, false, true},
        {std::string("0e9999\000", 7), "current", "", false, false, false},
        {std::string("0e9999\000", 7), "value", "", false, false, false},
        {std::string("0e9999\000", 7), "min", "-1", true, false, false},
        {std::string("0e9999\000", 7), "max", "1.5", false, true, true},
        {std::string("0e9999\000", 7), "step", "1.5", false, false, true},
        {std::string("0e9999０", 9), "current", "", false, false, false},
        {std::string("0e9999０", 9), "value", "", false, false, false},
        {std::string("0e9999０", 9), "min", "-1", true, false, false},
        {std::string("0e9999０", 9), "max", "1.5", false, true, true},
        {std::string("0e9999０", 9), "step", "1.5", false, false, true},
        {std::string("0e9999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999", 1002), "current", "0e9999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999", false, false, false},
        {std::string("0e9999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999", 1002), "value", "0e9999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999", false, false, false},
        {std::string("0e9999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999", 1002), "min", "-1", true, false, false},
        {std::string("0e9999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999", 1002), "max", "1.5", false, true, true},
        {std::string("0e9999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999", 1002), "step", "1.5", false, false, true},
        {std::string("0e9999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999x", 1003), "current", "", false, false, false},
        {std::string("0e9999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999x", 1003), "value", "", false, false, false},
        {std::string("0e9999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999x", 1003), "min", "-1", true, false, false},
        {std::string("0e9999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999x", 1003), "max", "1.5", false, true, true},
        {std::string("0e9999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999x", 1003), "step", "1.5", false, false, true},
    };
    for (const auto& row : exponents) {
        auto e = make_ref<Element>("input");
        e->set_attribute("type", "number");
        const std::string attribute(row.attribute);
        if (attribute == "current") e->set_form_value(row.raw);
        else {
            e->set_attribute(attribute, row.raw);
            if (attribute != "value") e->set_form_value(attribute == "min" ? "-1" : "1.5");
        }
        CHECK(e->form_value() == row.sanitized);
        const auto validity = form_number_validity(*e);
        CHECK(validity.range_underflow == row.underflow);
        CHECK(validity.range_overflow == row.overflow);
        CHECK(validity.step_mismatch == row.mismatch);
    }

}

void test_form_email_validation() {
    struct Case { const char* raw; const char* sanitized; bool multiple, mismatch; };
    const Case cases[] = {
        {"", "", false, false},
        {"a", "a", false, true},
        {"a@b", "a@b", false, false},
        {"a@b.c", "a@b.c", false, false},
        {"a@-b", "a@-b", false, true},
        {"a@b-", "a@b-", false, true},
        {"a@b_c", "a@b_c", false, true},
        {"a@b..c", "a@b..c", false, true},
        {"a@b.", "a@b.", false, true},
        {".a@b", ".a@b", false, false},
        {"a..b@c", "a..b@c", false, false},
        {"a.@b", "a.@b", false, false},
        {"!#$%&'*+-/=?^_`{|}~@b", "!#$%&'*+-/=?^_`{|}~@b", false, false},
        {"a b@c", "a b@c", false, true},
        {"a@@b", "a@@b", false, true},
        {"a@bücher.de", "a@bücher.de", false, true},
        {"a@xn--bcher-kva.de", "a@xn--bcher-kva.de", false, false},
        {"é@b", "é@b", false, true},
        {"a@[127.0.0.1]", "a@[127.0.0.1]", false, true},
        {"a@bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "a@bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", false, false},
        {"a@bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "a@bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", false, true},
        {"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa@b", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa@b", false, false},
        {"a@b,c@d", "a@b,c@d", false, true},
        {"a@b,", "a@b,", false, true},
        {" , ", ",", false, true},
        {" a@b \n", "a@b", false, false},
        {"a@b, c@d", "a@b, c@d", false, true},
        {"a@b,,c@d", "a@b,,c@d", false, true},
        {"", "", true, false},
        {"a", "a", true, true},
        {"a@b", "a@b", true, false},
        {"a@b.c", "a@b.c", true, false},
        {"a@-b", "a@-b", true, true},
        {"a@b-", "a@b-", true, true},
        {"a@b_c", "a@b_c", true, true},
        {"a@b..c", "a@b..c", true, true},
        {"a@b.", "a@b.", true, true},
        {".a@b", ".a@b", true, false},
        {"a..b@c", "a..b@c", true, false},
        {"a.@b", "a.@b", true, false},
        {"!#$%&'*+-/=?^_`{|}~@b", "!#$%&'*+-/=?^_`{|}~@b", true, false},
        {"a b@c", "a b@c", true, true},
        {"a@@b", "a@@b", true, true},
        {"a@bücher.de", "a@bücher.de", true, true},
        {"a@xn--bcher-kva.de", "a@xn--bcher-kva.de", true, false},
        {"é@b", "é@b", true, true},
        {"a@[127.0.0.1]", "a@[127.0.0.1]", true, true},
        {"a@bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "a@bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", true, false},
        {"a@bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "a@bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", true, true},
        {"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa@b", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa@b", true, false},
        {"a@b,c@d", "a@b,c@d", true, false},
        {"a@b,", "a@b,", true, true},
        {" , ", ",", true, true},
        {" a@b \n", "a@b", true, false},
        {"a@b, c@d", "a@b,c@d", true, false},
        {"a@b,,c@d", "a@b,,c@d", true, true},
    };
    for (const auto& row : cases) {
        auto e = make_ref<Element>("input");
        e->set_attribute("type", "email");
        if (row.multiple) e->set_attribute("multiple", "");
        e->set_form_value(row.raw);
        CHECK(e->form_value() == row.sanitized);
        CHECK(form_email_type_mismatch(*e) == row.mismatch);
        e->set_attribute("disabled", "");
        CHECK(form_email_type_mismatch(*e) == row.mismatch);
        e->set_attribute("type", "text");
        CHECK(!form_email_type_mismatch(*e));
    }
}

void test_form_url_validation() {
    struct Case { std::string raw, sanitized; bool mismatch; };
    const Case cases[] = {
        {{"", 0}, {"", 0}, false},
        {{"example.com", 11}, {"example.com", 11}, true},
        {{"/path", 5}, {"/path", 5}, true},
        {{"//example.com", 13}, {"//example.com", 13}, true},
        {{"https://example.com", 19}, {"https://example.com", 19}, false},
        {{"http:example.com", 16}, {"http:example.com", 16}, false},
        {{"https:/example.com", 18}, {"https:/example.com", 18}, false},
        {{"https:", 6}, {"https:", 6}, true},
        {{"http://", 7}, {"http://", 7}, true},
        {{"https://x:65535", 15}, {"https://x:65535", 15}, false},
        {{"https://x:65536", 15}, {"https://x:65536", 15}, true},
        {{"https://x:-1", 12}, {"https://x:-1", 12}, true},
        {{"https://x:abc", 13}, {"https://x:abc", 13}, true},
        {{"https://[::1]/", 14}, {"https://[::1]/", 14}, false},
        {{"https://[::1", 12}, {"https://[::1", 12}, true},
        {{"https://[gg::1]/", 16}, {"https://[gg::1]/", 16}, true},
        {{"http://127.1", 12}, {"http://127.1", 12}, false},
        {{"http://256.1.1.1", 16}, {"http://256.1.1.1", 16}, true},
        {{"http://0x7f000001", 17}, {"http://0x7f000001", 17}, false},
        {{"http://1.2.3.999", 16}, {"http://1.2.3.999", 16}, true},
        {{"http://b\303\274cher.de", 17}, {"http://b\303\274cher.de", 17}, false},
        {{"http://\342\230\203.net", 14}, {"http://\342\230\203.net", 14}, false},
        // URL Standard forbids domain spaces; Chrome 152 accepts this case.
        {{"http://a b", 10}, {"http://a b", 10}, true},
        {{"http://a%20b", 12}, {"http://a%20b", 12}, true},
        {{"http://%41.com", 14}, {"http://%41.com", 14}, false},
        {{"http://%zz.com", 14}, {"http://%zz.com", 14}, true},
        {{"http://x/%zz", 12}, {"http://x/%zz", 12}, false},
        {{"http://x/a b", 12}, {"http://x/a b", 12}, false},
        {{"https://user:pass@x/", 20}, {"https://user:pass@x/", 20}, false},
        {{"https://@/", 10}, {"https://@/", 10}, true},
        {{"file:///C:/game", 15}, {"file:///C:/game", 15}, false},
        {{"file://server/path", 18}, {"file://server/path", 18}, false},
        {{"file://", 7}, {"file://", 7}, false},
        {{"mailto:player@example.com", 25}, {"mailto:player@example.com", 25}, false},
        {{"urn:game:server:1", 17}, {"urn:game:server:1", 17}, false},
        {{"steam://connect/127.0.0.1", 25}, {"steam://connect/127.0.0.1", 25}, false},
        {{"data:text/plain,hello world", 27}, {"data:text/plain,hello world", 27}, false},
        {{"about:blank", 11}, {"about:blank", 11}, false},
        {{"javascript:alert(1)", 19}, {"javascript:alert(1)", 19}, false},
        {{"foo:", 4}, {"foo:", 4}, false},
        {{"foo:hello world", 15}, {"foo:hello world", 15}, false},
        {{"foo://x:99999", 13}, {"foo://x:99999", 13}, true},
        {{"foo://[bad]", 11}, {"foo://[bad]", 11}, true},
        {{"1foo:bar", 8}, {"1foo:bar", 8}, true},
        {{"a+b.c-d:foo", 11}, {"a+b.c-d:foo", 11}, false},
        {{" https://x \012", 12}, {"https://x", 9}, false},
        {{"ht\011tp://x", 9}, {"ht\011tp://x", 9}, false},
        {{"http:\134\134x", 8}, {"http:\134\134x", 8}, false},
        {{"https://x\000", 10}, {"https://x\000", 10}, false},
        {{"https://x\001", 10}, {"https://x\001", 10}, false},
        {{"http://x.", 9}, {"http://x.", 9}, false},
        {{"http://-x", 9}, {"http://-x", 9}, false},
        {{"http://a_b", 10}, {"http://a_b", 10}, false},
        {{"http://a..b", 11}, {"http://a..b", 11}, false},
    };
    for (const auto& row : cases) {
        auto e = make_ref<Element>("input");
        e->set_attribute("type", "url");
        e->set_form_value(row.raw);
        CHECK(e->form_value() == row.sanitized);
        if (form_url_type_mismatch(*e) != row.mismatch)
            std::fprintf(stderr, "URL validity differs for %s\n", row.raw.c_str());
        CHECK(form_url_type_mismatch(*e) == row.mismatch);
        e->set_attribute("disabled", "");
        CHECK(form_url_type_mismatch(*e) == row.mismatch);
        e->set_attribute("type", "text");
        CHECK(!form_url_type_mismatch(*e));
    }
}

void test_form_length_validation() {
    struct Case { const char *tag, *minimum, *maximum, *value; bool short_value, long_value; };
    const Case cases[] = {
        {"input", "5", nullptr, "ab", true, false},
        {"input", "2", nullptr, "🐎", false, false},
        {"input", "3", nullptr, "🐎", true, false},
        {"input", "3", nullptr, "é", true, false},
        {"input", "1", nullptr, "", false, false},
        {"input", nullptr, "1", "ab", false, true},
        {"input", nullptr, "1", "🐎", false, true},
        {"input", "  +3x", nullptr, "ab", true, false},
        {"input", "-0", nullptr, "ab", false, false},
        {"input", "2147483648", nullptr, "ab", false, false},
        {"textarea", "5", nullptr, "ab", true, false},
        {"textarea", "2", nullptr, "🐎", false, false},
        {"textarea", "3", nullptr, "🐎", true, false},
        {"textarea", "3", nullptr, "é", true, false},
        {"textarea", "1", nullptr, "", false, false},
        {"textarea", nullptr, "1", "ab", false, true},
        {"textarea", nullptr, "1", "🐎", false, true},
        {"textarea", "  +3x", nullptr, "ab", true, false},
        {"textarea", "-0", nullptr, "ab", false, false},
        {"textarea", "2147483648", nullptr, "ab", false, false},
    };
    for (const auto& row : cases) {
        auto e = make_ref<Element>(row.tag);
        if (row.minimum) e->set_attribute("minlength", row.minimum);
        if (row.maximum) e->set_attribute("maxlength", row.maximum);
        e->set_form_value(row.value);
        CHECK(form_text_length_validity(*e).valid());
        e->set_form_value(row.value, true, true);
        auto validity = form_text_length_validity(*e);
        CHECK(validity.too_short == row.short_value);
        CHECK(validity.too_long == row.long_value);
        e->reset_form_control();
        CHECK(form_text_length_validity(*e).valid());
    }
    for (const char* tag : {"input", "textarea"}) {
        auto e = make_ref<Element>(tag);
        e->set_attribute("minlength", "5");
        e->set_form_value("ab", true, true);
        CHECK(form_text_length_validity(*e).too_short);
        auto clone = make_ref<Element>(tag);
        clone->set_attribute("minlength", "5");
        clone->copy_form_state_from(*e);
        CHECK(form_text_length_validity(*clone).too_short);
        const auto version = e->form_version();
        e->set_form_value("ab");
        CHECK(form_text_length_validity(*e).too_short == (std::string_view(tag) == "textarea"));
        CHECK((e->form_version() != version) == (std::string_view(tag) == "input"));
        e->set_form_value("z");
        CHECK(form_text_length_validity(*e).valid());
    }
}

void test_form_length_edit_origin() {
    struct Case { const char *tag, *action, *value; bool short_value, candidate; };
    const Case cases[] = {
        {"input", "keep", "ab", true, true},
        {"input", "same-script", "ab", false, true},
        {"input", "other-script", "z", false, true},
        {"input", "clone", "ab", true, true},
        {"input", "reset", "", false, true},
        {"input", "default", "ab", true, true},
        {"input", "disable", "ab", true, false},
        {"input", "readonly", "ab", true, false},
        {"textarea", "keep", "ab", true, true},
        {"textarea", "same-script", "ab", true, true},
        {"textarea", "other-script", "z", false, true},
        {"textarea", "clone", "ab", true, true},
        {"textarea", "reset", "", false, true},
        {"textarea", "default", "ab", true, true},
        {"textarea", "disable", "ab", false, false},
        {"textarea", "readonly", "ab", false, false},
    };
    for (const auto& row : cases) {
        auto e = make_ref<Element>(row.tag);
        e->set_attribute("minlength", "5");
        e->set_form_value("ab", true, true);
        const std::string_view action(row.action);
        if (action == "same-script") e->set_form_value("ab");
        if (action == "other-script") e->set_form_value("z");
        if (action == "reset") e->reset_form_control();
        if (action == "disable") e->set_attribute("disabled", "");
        if (action == "readonly") e->set_attribute("readonly", "");
        if (action == "default") {
            if (std::string_view(row.tag) == "input") e->set_attribute("value", "q");
            else e->append_child(make_ref<TextNode>("q").get());
        }
        if (action == "clone") {
            auto clone = make_ref<Element>(row.tag);
            clone->set_attribute("minlength", "5");
            clone->copy_form_state_from(*e);
            e = clone;
        }
        CHECK(e->form_value() == row.value);
        CHECK(form_text_length_validity(*e).too_short == row.short_value);
        CHECK(form_is_validation_candidate(*e) == row.candidate);
    }
}

void test_form_temporal_values() {
    struct Case { const char *type, *raw, *value; bool valid; int64_t number; };
    const Case cases[] = {
        {"date", "", "", false, 0LL},
        {"date", "2024-02-29", "2024-02-29", true, 1709164800000LL},
        {"date", "2023-02-29", "", false, 0LL},
        {"date", "1900-02-29", "", false, 0LL},
        {"date", "2000-02-29", "2000-02-29", true, 951782400000LL},
        {"date", "2024-04-31", "", false, 0LL},
        {"date", "0000-01-01", "", false, 0LL},
        {"date", "0001-01-01", "0001-01-01", true, -62135596800000LL},
        {"date", "001-01-01", "", false, 0LL},
        {"date", "00001-01-01", "00001-01-01", true, -62135596800000LL},
        {"date", "2024-1-01", "", false, 0LL},
        {"date", "2024-01-1", "", false, 0LL},
        {"date", " 2024-01-01", "", false, 0LL},
        {"date", "2024-01-01Z", "", false, 0LL},
        {"date", "275760-09-13", "275760-09-13", true, 8640000000000000LL},
        {"date", "275760-09-14", "", false, 0LL},
        {"date", "999999-01-01", "", false, 0LL},
        {"month", "", "", false, 0LL},
        {"month", "2024-01", "2024-01", true, 648LL},
        {"month", "2024-00", "", false, 0LL},
        {"month", "2024-13", "", false, 0LL},
        {"month", "2024-1", "", false, 0LL},
        {"month", "0000-01", "", false, 0LL},
        {"month", "0001-01", "0001-01", true, -23628LL},
        {"month", "00001-01", "00001-01", true, -23628LL},
        {"month", "275760-09", "275760-09", true, 3285488LL},
        {"month", "275760-10", "", false, 0LL},
        {"month", "999999-01", "", false, 0LL},
        {"week", "", "", false, 0LL},
        {"week", "2020-W53", "2020-W53", true, 1609113600000LL},
        {"week", "2021-W53", "", false, 0LL},
        {"week", "2024-W01", "2024-W01", true, 1704067200000LL},
        {"week", "2024-W00", "", false, 0LL},
        {"week", "2024-W54", "", false, 0LL},
        {"week", "2024-w01", "", false, 0LL},
        {"week", "2024-W1", "", false, 0LL},
        {"week", "0001-W01", "0001-W01", true, -62135596800000LL},
        {"week", "0000-W01", "", false, 0LL},
        {"week", "00001-W01", "00001-W01", true, -62135596800000LL},
        {"week", "275760-W37", "275760-W37", true, 8639999568000000LL},
        {"week", "275760-W38", "", false, 0LL},
        {"time", "", "", false, 0LL},
        {"time", "00:00", "00:00", true, 0LL},
        {"time", "23:59", "23:59", true, 86340000LL},
        {"time", "24:00", "", false, 0LL},
        {"time", "12:60", "", false, 0LL},
        {"time", "12:34:56", "12:34:56", true, 45296000LL},
        {"time", "12:34:60", "", false, 0LL},
        {"time", "12:34:00", "12:34:00", true, 45240000LL},
        {"time", "12:34:00.000", "12:34:00.000", true, 45240000LL},
        {"time", "12:34:00.1", "12:34:00.1", true, 45240100LL},
        {"time", "12:34:00.12", "12:34:00.12", true, 45240120LL},
        {"time", "12:34:00.123", "12:34:00.123", true, 45240123LL},
        {"time", "12:34:00.1234", "", false, 0LL},
        {"time", "12:34.5", "", false, 0LL},
        {"time", "1:02", "", false, 0LL},
        {"time", "01:2", "", false, 0LL},
        {"time", "12:34Z", "", false, 0LL},
        {"datetime-local", "", "", false, 0LL},
        {"datetime-local", "2024-02-29T12:34", "2024-02-29T12:34", true, 1709210040000LL},
        {"datetime-local", "2024-02-29 12:34", "2024-02-29T12:34", true, 1709210040000LL},
        {"datetime-local", "2024-02-29T12:34:00", "2024-02-29T12:34", true, 1709210040000LL},
        {"datetime-local", "2024-02-29T12:34:00.000", "2024-02-29T12:34", true, 1709210040000LL},
        {"datetime-local", "2024-02-29T12:34:00.120", "2024-02-29T12:34:00.12", true, 1709210040120LL},
        {"datetime-local", "2024-02-29T12:34:01.100", "2024-02-29T12:34:01.1", true, 1709210041100LL},
        {"datetime-local", "2023-02-29T12:34", "", false, 0LL},
        {"datetime-local", "00001-01-01T00:00", "0001-01-01T00:00", true, -62135596800000LL},
        {"datetime-local", "275760-09-13T00:00", "275760-09-13T00:00", true, 8640000000000000LL},
        {"datetime-local", "275760-09-13T00:01", "", false, 0LL},
        {"datetime-local", "2024-01-01t00:00", "", false, 0LL},
        {"datetime-local", "2024-01-01T00:00Z", "", false, 0LL},
    };
    for (const auto& row : cases) {
        int64_t number = 0;
        CHECK(parse_temporal_value(row.type, row.raw, &number) == row.valid);
        if (row.valid) CHECK(number == row.number);
        CHECK(sanitize_temporal_value(row.type, row.raw) == row.value);
        auto e = make_ref<Element>("input");
        e->set_attribute("type", row.type);
        e->set_attribute("value", row.raw);
        CHECK(e->form_value() == row.value);
        e->set_form_value("not a date");
        CHECK(e->form_value().empty());
        e->reset_form_control();
        CHECK(e->form_value() == row.value);
        e->set_attribute("type", "text");
        e->set_form_value(row.raw);
        e->set_attribute("type", row.type);
        CHECK(e->form_value() == row.value);
        Form form("<form id=f><input id=c required></form>");
        form.attr("#c", "type", row.type);
        form.set("#c", row.raw);
        CHECK(form.value("#c") == row.value);
    }
}

void test_form_temporal_constraints() {
    struct Case { const char *type, *value, *minimum, *maximum, *step, *initial; bool underflow, overflow, mismatch; };
    const Case cases[] = {
        {"date", "1970-01-01", nullptr, nullptr, nullptr, nullptr, false, false, false},
        {"date", "1970-01-02", nullptr, nullptr, nullptr, nullptr, false, false, false},
        {"date", "1970-01-03", nullptr, nullptr, nullptr, nullptr, false, false, false},
        {"date", "1970-01-01", nullptr, nullptr, "any", nullptr, false, false, false},
        {"date", "1970-01-02", nullptr, nullptr, "any", nullptr, false, false, false},
        {"date", "1970-01-03", nullptr, nullptr, "any", nullptr, false, false, false},
        {"date", "1970-01-01", nullptr, nullptr, "2", nullptr, false, false, false},
        {"date", "1970-01-02", nullptr, nullptr, "2", nullptr, false, false, true},
        {"date", "1970-01-03", nullptr, nullptr, "2", nullptr, false, false, false},
        {"date", "1970-01-01", nullptr, nullptr, "1.5", nullptr, false, false, false},
        {"date", "1970-01-02", nullptr, nullptr, "1.5", nullptr, false, false, true},
        {"date", "1970-01-03", nullptr, nullptr, "1.5", nullptr, false, false, false},
        {"date", "1970-01-01", nullptr, nullptr, "0.0001", nullptr, false, false, false},
        {"date", "1970-01-02", nullptr, nullptr, "0.0001", nullptr, false, false, false},
        {"date", "1970-01-03", nullptr, nullptr, "0.0001", nullptr, false, false, false},
        {"month", "1970-01", nullptr, nullptr, nullptr, nullptr, false, false, false},
        {"month", "1970-02", nullptr, nullptr, nullptr, nullptr, false, false, false},
        {"month", "1970-03", nullptr, nullptr, nullptr, nullptr, false, false, false},
        {"month", "1970-01", nullptr, nullptr, "any", nullptr, false, false, false},
        {"month", "1970-02", nullptr, nullptr, "any", nullptr, false, false, false},
        {"month", "1970-03", nullptr, nullptr, "any", nullptr, false, false, false},
        {"month", "1970-01", nullptr, nullptr, "2", nullptr, false, false, false},
        {"month", "1970-02", nullptr, nullptr, "2", nullptr, false, false, true},
        {"month", "1970-03", nullptr, nullptr, "2", nullptr, false, false, false},
        {"month", "1970-01", nullptr, nullptr, "1.5", nullptr, false, false, false},
        {"month", "1970-02", nullptr, nullptr, "1.5", nullptr, false, false, true},
        {"month", "1970-03", nullptr, nullptr, "1.5", nullptr, false, false, false},
        {"month", "1970-01", nullptr, nullptr, "0.0001", nullptr, false, false, false},
        {"month", "1970-02", nullptr, nullptr, "0.0001", nullptr, false, false, false},
        {"month", "1970-03", nullptr, nullptr, "0.0001", nullptr, false, false, false},
        {"week", "1970-W01", nullptr, nullptr, nullptr, nullptr, false, false, false},
        {"week", "1970-W02", nullptr, nullptr, nullptr, nullptr, false, false, false},
        {"week", "1970-W03", nullptr, nullptr, nullptr, nullptr, false, false, false},
        {"week", "1970-W01", nullptr, nullptr, "any", nullptr, false, false, false},
        {"week", "1970-W02", nullptr, nullptr, "any", nullptr, false, false, false},
        {"week", "1970-W03", nullptr, nullptr, "any", nullptr, false, false, false},
        {"week", "1970-W01", nullptr, nullptr, "2", nullptr, false, false, false},
        {"week", "1970-W02", nullptr, nullptr, "2", nullptr, false, false, true},
        {"week", "1970-W03", nullptr, nullptr, "2", nullptr, false, false, false},
        {"week", "1970-W01", nullptr, nullptr, "1.5", nullptr, false, false, false},
        {"week", "1970-W02", nullptr, nullptr, "1.5", nullptr, false, false, true},
        {"week", "1970-W03", nullptr, nullptr, "1.5", nullptr, false, false, false},
        {"week", "1970-W01", nullptr, nullptr, "0.0001", nullptr, false, false, false},
        {"week", "1970-W02", nullptr, nullptr, "0.0001", nullptr, false, false, false},
        {"week", "1970-W03", nullptr, nullptr, "0.0001", nullptr, false, false, false},
        {"time", "00:00", nullptr, nullptr, nullptr, nullptr, false, false, false},
        {"time", "00:00:01", nullptr, nullptr, nullptr, nullptr, false, false, true},
        {"time", "00:01", nullptr, nullptr, nullptr, nullptr, false, false, false},
        {"time", "00:00", nullptr, nullptr, "any", nullptr, false, false, false},
        {"time", "00:00:01", nullptr, nullptr, "any", nullptr, false, false, false},
        {"time", "00:01", nullptr, nullptr, "any", nullptr, false, false, false},
        {"time", "00:00", nullptr, nullptr, "2", nullptr, false, false, false},
        {"time", "00:00:01", nullptr, nullptr, "2", nullptr, false, false, true},
        {"time", "00:01", nullptr, nullptr, "2", nullptr, false, false, false},
        {"time", "00:00", nullptr, nullptr, "1.5", nullptr, false, false, false},
        {"time", "00:00:01", nullptr, nullptr, "1.5", nullptr, false, false, true},
        {"time", "00:01", nullptr, nullptr, "1.5", nullptr, false, false, false},
        {"time", "00:00", nullptr, nullptr, "0.0001", nullptr, false, false, false},
        {"time", "00:00:01", nullptr, nullptr, "0.0001", nullptr, false, false, false},
        {"time", "00:01", nullptr, nullptr, "0.0001", nullptr, false, false, false},
        {"datetime-local", "1970-01-01T00:00", nullptr, nullptr, nullptr, nullptr, false, false, false},
        {"datetime-local", "1970-01-01T00:00:01", nullptr, nullptr, nullptr, nullptr, false, false, true},
        {"datetime-local", "1970-01-01T00:01", nullptr, nullptr, nullptr, nullptr, false, false, false},
        {"datetime-local", "1970-01-01T00:00", nullptr, nullptr, "any", nullptr, false, false, false},
        {"datetime-local", "1970-01-01T00:00:01", nullptr, nullptr, "any", nullptr, false, false, false},
        {"datetime-local", "1970-01-01T00:01", nullptr, nullptr, "any", nullptr, false, false, false},
        {"datetime-local", "1970-01-01T00:00", nullptr, nullptr, "2", nullptr, false, false, false},
        {"datetime-local", "1970-01-01T00:00:01", nullptr, nullptr, "2", nullptr, false, false, true},
        {"datetime-local", "1970-01-01T00:01", nullptr, nullptr, "2", nullptr, false, false, false},
        {"datetime-local", "1970-01-01T00:00", nullptr, nullptr, "1.5", nullptr, false, false, false},
        {"datetime-local", "1970-01-01T00:00:01", nullptr, nullptr, "1.5", nullptr, false, false, true},
        {"datetime-local", "1970-01-01T00:01", nullptr, nullptr, "1.5", nullptr, false, false, false},
        {"datetime-local", "1970-01-01T00:00", nullptr, nullptr, "0.0001", nullptr, false, false, false},
        {"datetime-local", "1970-01-01T00:00:01", nullptr, nullptr, "0.0001", nullptr, false, false, false},
        {"datetime-local", "1970-01-01T00:01", nullptr, nullptr, "0.0001", nullptr, false, false, false},
        {"time", "00:00", "22:00", "05:00", nullptr, nullptr, false, false, false},
        {"time", "05:00", "22:00", "05:00", nullptr, nullptr, false, false, false},
        {"time", "12:00", "22:00", "05:00", nullptr, nullptr, true, true, false},
        {"time", "22:00", "22:00", "05:00", nullptr, nullptr, false, false, false},
        {"time", "23:00", "22:00", "05:00", nullptr, nullptr, false, false, false},
        {"date", "1970-01-01", nullptr, nullptr, "0", nullptr, false, false, false},
        {"date", "1970-01-02", nullptr, nullptr, "0", nullptr, false, false, false},
        {"date", "1970-01-03", nullptr, nullptr, "0", nullptr, false, false, false},
        {"date", "1970-01-01", nullptr, nullptr, "-1", nullptr, false, false, false},
        {"date", "1970-01-02", nullptr, nullptr, "-1", nullptr, false, false, false},
        {"date", "1970-01-03", nullptr, nullptr, "-1", nullptr, false, false, false},
        {"date", "1970-01-01", nullptr, nullptr, "bad", nullptr, false, false, false},
        {"date", "1970-01-02", nullptr, nullptr, "bad", nullptr, false, false, false},
        {"date", "1970-01-03", nullptr, nullptr, "bad", nullptr, false, false, false},
        {"date", "1970-01-01", nullptr, nullptr, "AnY", nullptr, false, false, false},
        {"date", "1970-01-02", nullptr, nullptr, "AnY", nullptr, false, false, false},
        {"date", "1970-01-03", nullptr, nullptr, "AnY", nullptr, false, false, false},
        {"date", "1970-01-01", nullptr, nullptr, "0.4", nullptr, false, false, false},
        {"date", "1970-01-02", nullptr, nullptr, "0.4", nullptr, false, false, false},
        {"date", "1970-01-03", nullptr, nullptr, "0.4", nullptr, false, false, false},
        {"date", "1970-01-01", nullptr, nullptr, "0.5", nullptr, false, false, false},
        {"date", "1970-01-02", nullptr, nullptr, "0.5", nullptr, false, false, false},
        {"date", "1970-01-03", nullptr, nullptr, "0.5", nullptr, false, false, false},
        {"date", "1970-01-01", nullptr, nullptr, "1.49", nullptr, false, false, false},
        {"date", "1970-01-02", nullptr, nullptr, "1.49", nullptr, false, false, false},
        {"date", "1970-01-03", nullptr, nullptr, "1.49", nullptr, false, false, false},
        {"date", "1970-01-01", nullptr, nullptr, "0.0015", nullptr, false, false, false},
        {"date", "1970-01-02", nullptr, nullptr, "0.0015", nullptr, false, false, false},
        {"date", "1970-01-03", nullptr, nullptr, "0.0015", nullptr, false, false, false},
        {"date", "1970-01-01", nullptr, nullptr, "0.0001", nullptr, false, false, false},
        {"date", "1970-01-02", nullptr, nullptr, "0.0001", nullptr, false, false, false},
        {"date", "1970-01-03", nullptr, nullptr, "0.0001", nullptr, false, false, false},
        {"date", "1970-01-01", "1970-01-02", nullptr, "2", nullptr, true, false, true},
        {"date", "1970-01-02", "1970-01-02", nullptr, "2", nullptr, false, false, false},
        {"date", "1970-01-03", "1970-01-02", nullptr, "2", nullptr, false, false, true},
        {"date", "1970-01-01", nullptr, nullptr, "2", "1970-01-02", false, false, true},
        {"date", "1970-01-02", nullptr, nullptr, "2", "1970-01-02", false, false, false},
        {"date", "1970-01-03", nullptr, nullptr, "2", "1970-01-02", false, false, true},
        {"date", "1970-01-01", "bad", nullptr, "2", "1970-01-02", false, false, true},
        {"date", "1970-01-02", "bad", nullptr, "2", "1970-01-02", false, false, false},
        {"date", "1970-01-03", "bad", nullptr, "2", "1970-01-02", false, false, true},
        {"date", "1970-01-01", "1970-01-03", "1970-01-01", "any", nullptr, true, false, false},
        {"date", "1970-01-02", "1970-01-03", "1970-01-01", "any", nullptr, true, true, false},
        {"date", "1970-01-03", "1970-01-03", "1970-01-01", "any", nullptr, false, true, false},
        {"month", "1970-01", nullptr, nullptr, "0", nullptr, false, false, false},
        {"month", "1970-02", nullptr, nullptr, "0", nullptr, false, false, false},
        {"month", "1970-03", nullptr, nullptr, "0", nullptr, false, false, false},
        {"month", "1970-01", nullptr, nullptr, "-1", nullptr, false, false, false},
        {"month", "1970-02", nullptr, nullptr, "-1", nullptr, false, false, false},
        {"month", "1970-03", nullptr, nullptr, "-1", nullptr, false, false, false},
        {"month", "1970-01", nullptr, nullptr, "bad", nullptr, false, false, false},
        {"month", "1970-02", nullptr, nullptr, "bad", nullptr, false, false, false},
        {"month", "1970-03", nullptr, nullptr, "bad", nullptr, false, false, false},
        {"month", "1970-01", nullptr, nullptr, "AnY", nullptr, false, false, false},
        {"month", "1970-02", nullptr, nullptr, "AnY", nullptr, false, false, false},
        {"month", "1970-03", nullptr, nullptr, "AnY", nullptr, false, false, false},
        {"month", "1970-01", nullptr, nullptr, "0.4", nullptr, false, false, false},
        {"month", "1970-02", nullptr, nullptr, "0.4", nullptr, false, false, false},
        {"month", "1970-03", nullptr, nullptr, "0.4", nullptr, false, false, false},
        {"month", "1970-01", nullptr, nullptr, "0.5", nullptr, false, false, false},
        {"month", "1970-02", nullptr, nullptr, "0.5", nullptr, false, false, false},
        {"month", "1970-03", nullptr, nullptr, "0.5", nullptr, false, false, false},
        {"month", "1970-01", nullptr, nullptr, "1.49", nullptr, false, false, false},
        {"month", "1970-02", nullptr, nullptr, "1.49", nullptr, false, false, false},
        {"month", "1970-03", nullptr, nullptr, "1.49", nullptr, false, false, false},
        {"month", "1970-01", nullptr, nullptr, "0.0015", nullptr, false, false, false},
        {"month", "1970-02", nullptr, nullptr, "0.0015", nullptr, false, false, false},
        {"month", "1970-03", nullptr, nullptr, "0.0015", nullptr, false, false, false},
        {"month", "1970-01", nullptr, nullptr, "0.0001", nullptr, false, false, false},
        {"month", "1970-02", nullptr, nullptr, "0.0001", nullptr, false, false, false},
        {"month", "1970-03", nullptr, nullptr, "0.0001", nullptr, false, false, false},
        {"month", "1970-01", "1970-02", nullptr, "2", nullptr, true, false, true},
        {"month", "1970-02", "1970-02", nullptr, "2", nullptr, false, false, false},
        {"month", "1970-03", "1970-02", nullptr, "2", nullptr, false, false, true},
        {"month", "1970-01", nullptr, nullptr, "2", "1970-02", false, false, true},
        {"month", "1970-02", nullptr, nullptr, "2", "1970-02", false, false, false},
        {"month", "1970-03", nullptr, nullptr, "2", "1970-02", false, false, true},
        {"month", "1970-01", "bad", nullptr, "2", "1970-02", false, false, true},
        {"month", "1970-02", "bad", nullptr, "2", "1970-02", false, false, false},
        {"month", "1970-03", "bad", nullptr, "2", "1970-02", false, false, true},
        {"month", "1970-01", "1970-03", "1970-01", "any", nullptr, true, false, false},
        {"month", "1970-02", "1970-03", "1970-01", "any", nullptr, true, true, false},
        {"month", "1970-03", "1970-03", "1970-01", "any", nullptr, false, true, false},
        {"week", "1970-W01", nullptr, nullptr, "0", nullptr, false, false, false},
        {"week", "1970-W02", nullptr, nullptr, "0", nullptr, false, false, false},
        {"week", "1970-W03", nullptr, nullptr, "0", nullptr, false, false, false},
        {"week", "1970-W01", nullptr, nullptr, "-1", nullptr, false, false, false},
        {"week", "1970-W02", nullptr, nullptr, "-1", nullptr, false, false, false},
        {"week", "1970-W03", nullptr, nullptr, "-1", nullptr, false, false, false},
        {"week", "1970-W01", nullptr, nullptr, "bad", nullptr, false, false, false},
        {"week", "1970-W02", nullptr, nullptr, "bad", nullptr, false, false, false},
        {"week", "1970-W03", nullptr, nullptr, "bad", nullptr, false, false, false},
        {"week", "1970-W01", nullptr, nullptr, "AnY", nullptr, false, false, false},
        {"week", "1970-W02", nullptr, nullptr, "AnY", nullptr, false, false, false},
        {"week", "1970-W03", nullptr, nullptr, "AnY", nullptr, false, false, false},
        {"week", "1970-W01", nullptr, nullptr, "0.4", nullptr, false, false, false},
        {"week", "1970-W02", nullptr, nullptr, "0.4", nullptr, false, false, false},
        {"week", "1970-W03", nullptr, nullptr, "0.4", nullptr, false, false, false},
        {"week", "1970-W01", nullptr, nullptr, "0.5", nullptr, false, false, false},
        {"week", "1970-W02", nullptr, nullptr, "0.5", nullptr, false, false, false},
        {"week", "1970-W03", nullptr, nullptr, "0.5", nullptr, false, false, false},
        {"week", "1970-W01", nullptr, nullptr, "1.49", nullptr, false, false, false},
        {"week", "1970-W02", nullptr, nullptr, "1.49", nullptr, false, false, false},
        {"week", "1970-W03", nullptr, nullptr, "1.49", nullptr, false, false, false},
        {"week", "1970-W01", nullptr, nullptr, "0.0015", nullptr, false, false, false},
        {"week", "1970-W02", nullptr, nullptr, "0.0015", nullptr, false, false, false},
        {"week", "1970-W03", nullptr, nullptr, "0.0015", nullptr, false, false, false},
        {"week", "1970-W01", nullptr, nullptr, "0.0001", nullptr, false, false, false},
        {"week", "1970-W02", nullptr, nullptr, "0.0001", nullptr, false, false, false},
        {"week", "1970-W03", nullptr, nullptr, "0.0001", nullptr, false, false, false},
        {"week", "1970-W01", "1970-W02", nullptr, "2", nullptr, true, false, true},
        {"week", "1970-W02", "1970-W02", nullptr, "2", nullptr, false, false, false},
        {"week", "1970-W03", "1970-W02", nullptr, "2", nullptr, false, false, true},
        {"week", "1970-W01", nullptr, nullptr, "2", "1970-W02", false, false, true},
        {"week", "1970-W02", nullptr, nullptr, "2", "1970-W02", false, false, false},
        {"week", "1970-W03", nullptr, nullptr, "2", "1970-W02", false, false, true},
        {"week", "1970-W01", "bad", nullptr, "2", "1970-W02", false, false, true},
        {"week", "1970-W02", "bad", nullptr, "2", "1970-W02", false, false, false},
        {"week", "1970-W03", "bad", nullptr, "2", "1970-W02", false, false, true},
        {"week", "1970-W01", "1970-W03", "1970-W01", "any", nullptr, true, false, false},
        {"week", "1970-W02", "1970-W03", "1970-W01", "any", nullptr, true, true, false},
        {"week", "1970-W03", "1970-W03", "1970-W01", "any", nullptr, false, true, false},
        {"time", "00:00", nullptr, nullptr, "0", nullptr, false, false, false},
        {"time", "00:00:00.001", nullptr, nullptr, "0", nullptr, false, false, true},
        {"time", "00:00:00.002", nullptr, nullptr, "0", nullptr, false, false, true},
        {"time", "00:00", nullptr, nullptr, "-1", nullptr, false, false, false},
        {"time", "00:00:00.001", nullptr, nullptr, "-1", nullptr, false, false, true},
        {"time", "00:00:00.002", nullptr, nullptr, "-1", nullptr, false, false, true},
        {"time", "00:00", nullptr, nullptr, "bad", nullptr, false, false, false},
        {"time", "00:00:00.001", nullptr, nullptr, "bad", nullptr, false, false, true},
        {"time", "00:00:00.002", nullptr, nullptr, "bad", nullptr, false, false, true},
        {"time", "00:00", nullptr, nullptr, "AnY", nullptr, false, false, false},
        {"time", "00:00:00.001", nullptr, nullptr, "AnY", nullptr, false, false, false},
        {"time", "00:00:00.002", nullptr, nullptr, "AnY", nullptr, false, false, false},
        {"time", "00:00", nullptr, nullptr, "0.4", nullptr, false, false, false},
        {"time", "00:00:00.001", nullptr, nullptr, "0.4", nullptr, false, false, true},
        {"time", "00:00:00.002", nullptr, nullptr, "0.4", nullptr, false, false, true},
        {"time", "00:00", nullptr, nullptr, "0.5", nullptr, false, false, false},
        {"time", "00:00:00.001", nullptr, nullptr, "0.5", nullptr, false, false, true},
        {"time", "00:00:00.002", nullptr, nullptr, "0.5", nullptr, false, false, true},
        {"time", "00:00", nullptr, nullptr, "1.49", nullptr, false, false, false},
        {"time", "00:00:00.001", nullptr, nullptr, "1.49", nullptr, false, false, true},
        {"time", "00:00:00.002", nullptr, nullptr, "1.49", nullptr, false, false, true},
        {"time", "00:00", nullptr, nullptr, "0.0015", nullptr, false, false, false},
        {"time", "00:00:00.001", nullptr, nullptr, "0.0015", nullptr, false, false, true},
        {"time", "00:00:00.002", nullptr, nullptr, "0.0015", nullptr, false, false, false},
        {"time", "00:00", nullptr, nullptr, "0.0001", nullptr, false, false, false},
        {"time", "00:00:00.001", nullptr, nullptr, "0.0001", nullptr, false, false, false},
        {"time", "00:00:00.002", nullptr, nullptr, "0.0001", nullptr, false, false, false},
        {"time", "00:00", "00:00:00.001", nullptr, "0.002", nullptr, true, false, true},
        {"time", "00:00:00.001", "00:00:00.001", nullptr, "0.002", nullptr, false, false, false},
        {"time", "00:00:00.002", "00:00:00.001", nullptr, "0.002", nullptr, false, false, true},
        {"time", "00:00", nullptr, nullptr, "0.002", "00:00:00.001", false, false, true},
        {"time", "00:00:00.001", nullptr, nullptr, "0.002", "00:00:00.001", false, false, false},
        {"time", "00:00:00.002", nullptr, nullptr, "0.002", "00:00:00.001", false, false, true},
        {"time", "00:00", "bad", nullptr, "0.002", "00:00:00.001", false, false, true},
        {"time", "00:00:00.001", "bad", nullptr, "0.002", "00:00:00.001", false, false, false},
        {"time", "00:00:00.002", "bad", nullptr, "0.002", "00:00:00.001", false, false, true},
        {"time", "00:00", "00:00:00.002", "00:00", "any", nullptr, false, false, false},
        {"time", "00:00:00.001", "00:00:00.002", "00:00", "any", nullptr, true, true, false},
        {"time", "00:00:00.002", "00:00:00.002", "00:00", "any", nullptr, false, false, false},
        {"datetime-local", "1970-01-01T00:00", nullptr, nullptr, "0", nullptr, false, false, false},
        {"datetime-local", "1970-01-01T00:00:00.001", nullptr, nullptr, "0", nullptr, false, false, true},
        {"datetime-local", "1970-01-01T00:00:00.002", nullptr, nullptr, "0", nullptr, false, false, true},
        {"datetime-local", "1970-01-01T00:00", nullptr, nullptr, "-1", nullptr, false, false, false},
        {"datetime-local", "1970-01-01T00:00:00.001", nullptr, nullptr, "-1", nullptr, false, false, true},
        {"datetime-local", "1970-01-01T00:00:00.002", nullptr, nullptr, "-1", nullptr, false, false, true},
        {"datetime-local", "1970-01-01T00:00", nullptr, nullptr, "bad", nullptr, false, false, false},
        {"datetime-local", "1970-01-01T00:00:00.001", nullptr, nullptr, "bad", nullptr, false, false, true},
        {"datetime-local", "1970-01-01T00:00:00.002", nullptr, nullptr, "bad", nullptr, false, false, true},
        {"datetime-local", "1970-01-01T00:00", nullptr, nullptr, "AnY", nullptr, false, false, false},
        {"datetime-local", "1970-01-01T00:00:00.001", nullptr, nullptr, "AnY", nullptr, false, false, false},
        {"datetime-local", "1970-01-01T00:00:00.002", nullptr, nullptr, "AnY", nullptr, false, false, false},
        {"datetime-local", "1970-01-01T00:00", nullptr, nullptr, "0.4", nullptr, false, false, false},
        {"datetime-local", "1970-01-01T00:00:00.001", nullptr, nullptr, "0.4", nullptr, false, false, true},
        {"datetime-local", "1970-01-01T00:00:00.002", nullptr, nullptr, "0.4", nullptr, false, false, true},
        {"datetime-local", "1970-01-01T00:00", nullptr, nullptr, "0.5", nullptr, false, false, false},
        {"datetime-local", "1970-01-01T00:00:00.001", nullptr, nullptr, "0.5", nullptr, false, false, true},
        {"datetime-local", "1970-01-01T00:00:00.002", nullptr, nullptr, "0.5", nullptr, false, false, true},
        {"datetime-local", "1970-01-01T00:00", nullptr, nullptr, "1.49", nullptr, false, false, false},
        {"datetime-local", "1970-01-01T00:00:00.001", nullptr, nullptr, "1.49", nullptr, false, false, true},
        {"datetime-local", "1970-01-01T00:00:00.002", nullptr, nullptr, "1.49", nullptr, false, false, true},
        {"datetime-local", "1970-01-01T00:00", nullptr, nullptr, "0.0015", nullptr, false, false, false},
        {"datetime-local", "1970-01-01T00:00:00.001", nullptr, nullptr, "0.0015", nullptr, false, false, true},
        {"datetime-local", "1970-01-01T00:00:00.002", nullptr, nullptr, "0.0015", nullptr, false, false, false},
        {"datetime-local", "1970-01-01T00:00", nullptr, nullptr, "0.0001", nullptr, false, false, false},
        {"datetime-local", "1970-01-01T00:00:00.001", nullptr, nullptr, "0.0001", nullptr, false, false, false},
        {"datetime-local", "1970-01-01T00:00:00.002", nullptr, nullptr, "0.0001", nullptr, false, false, false},
        {"datetime-local", "1970-01-01T00:00", "1970-01-01T00:00:00.001", nullptr, "0.002", nullptr, true, false, true},
        {"datetime-local", "1970-01-01T00:00:00.001", "1970-01-01T00:00:00.001", nullptr, "0.002", nullptr, false, false, false},
        {"datetime-local", "1970-01-01T00:00:00.002", "1970-01-01T00:00:00.001", nullptr, "0.002", nullptr, false, false, true},
        {"datetime-local", "1970-01-01T00:00", nullptr, nullptr, "0.002", "1970-01-01T00:00:00.001", false, false, true},
        {"datetime-local", "1970-01-01T00:00:00.001", nullptr, nullptr, "0.002", "1970-01-01T00:00:00.001", false, false, false},
        {"datetime-local", "1970-01-01T00:00:00.002", nullptr, nullptr, "0.002", "1970-01-01T00:00:00.001", false, false, true},
        {"datetime-local", "1970-01-01T00:00", "bad", nullptr, "0.002", "1970-01-01T00:00:00.001", false, false, true},
        {"datetime-local", "1970-01-01T00:00:00.001", "bad", nullptr, "0.002", "1970-01-01T00:00:00.001", false, false, false},
        {"datetime-local", "1970-01-01T00:00:00.002", "bad", nullptr, "0.002", "1970-01-01T00:00:00.001", false, false, true},
        {"datetime-local", "1970-01-01T00:00", "1970-01-01T00:00:00.002", "1970-01-01T00:00", "any", nullptr, true, false, false},
        {"datetime-local", "1970-01-01T00:00:00.001", "1970-01-01T00:00:00.002", "1970-01-01T00:00", "any", nullptr, true, true, false},
        {"datetime-local", "1970-01-01T00:00:00.002", "1970-01-01T00:00:00.002", "1970-01-01T00:00", "any", nullptr, false, true, false},
    };
    for (const auto& row : cases) {
        auto e = make_ref<Element>("input");
        e->set_attribute("type", row.type);
        if (row.minimum) e->set_attribute("min", row.minimum);
        if (row.maximum) e->set_attribute("max", row.maximum);
        if (row.step) e->set_attribute("step", row.step);
        if (row.initial) e->set_attribute("value", row.initial);
        e->set_form_value(row.value);
        const auto validity = form_temporal_validity(*e);
        if (validity.range_underflow != row.underflow || validity.range_overflow != row.overflow || validity.step_mismatch != row.mismatch)
            std::fprintf(stderr, "Temporal constraint mismatch: %s %s step=%s\n", row.type, row.value, row.step ? row.step : "default");
        CHECK(validity.range_underflow == row.underflow);
        CHECK(validity.range_overflow == row.overflow);
        CHECK(validity.step_mismatch == row.mismatch);
    }
}

void test_form_number_edit_buffer() {
    struct Case { const char *raw, *value; bool bad; };
    const Case cases[] = {{"", "", false}, {"-", "", true}, {"+", "", true},
        {"1e", "", true}, {"1e-", "", true}, {"1.", "", true},
        {"+1", "1", false}, {"01", "01", false}, {".5", ".5", false},
        {"-.5", "-.5", false}, {"1e+2", "1e+2", false}, {"1e999", "", true}};
    for (const auto& row : cases) {
        auto e = make_ref<Element>("input"); e->set_attribute("type", "number");
        e->set_attribute("required", "");
        e->set_form_value(row.raw, false, true);
        CHECK(e->form_edit_value() == row.raw);
        CHECK(e->form_value() == row.value);
        CHECK(e->form_bad_input() == row.bad);
        CHECK(form_required_value_missing(*e) == std::string_view(row.value).empty());
        const char* retained = e->form_edit_value().data();
        const auto version = e->form_version();
        e->set_form_value(row.raw, false, true);
        CHECK(e->form_version() == version);
        CHECK(e->form_edit_value().data() == retained);
        e->set_attribute("min", "0"); e->set_attribute("max", "100"); e->set_attribute("step", "any");
        CHECK(e->form_edit_value() == row.raw);
        CHECK(e->form_bad_input() == row.bad);
        auto clone = make_ref<Element>("input"); clone->set_attribute("type", "number");
        clone->copy_form_state_from(*e);
        CHECK(clone->form_value() == row.value); CHECK(!clone->form_bad_input());
        CHECK(clone->form_edit_value() == row.value);
        e->set_attribute("type", "text");
        CHECK(e->form_value() == row.value); CHECK(!e->form_bad_input());
        e->set_attribute("type", "number");
        e->set_form_value(row.raw, false, true);
        e->set_form_value("");
        CHECK(e->form_edit_value().empty()); CHECK(!e->form_bad_input());
        e->set_form_value(row.raw, false, true);
        e->reset_form_control();
        CHECK(e->form_edit_value().empty()); CHECK(!e->form_bad_input());
    }
    for (const auto& row : cases) for (bool bypass : {false, true}) {
        Form f("<dialog id=d><form id=f method=dialog><input id=c type=number step=any><button id=s>OK</button></form></dialog>");
        weva_element_show_dialog(f.doc, f.at("#d"), 1);
        weva_document_update(f.doc, 0);
        weva_document_set_focus(f.doc, f.at("#c"));
        if (*row.raw) CHECK(weva_document_try_text_input(f.doc, row.raw));
        CHECK(f.value("#c") == row.value);
        weva_event event{};
        while (weva_document_poll_event(f.doc, &event))
            if (event.kind == WEVA_EVENT_VALUE_CHANGED) CHECK(std::string(event.text) == row.value);
        if (bypass) f.attr("#f", "novalidate", "");
        weva_document_set_focus(f.doc, f.at("#s"));
        weva_document_key(f.doc, WEVA_KEY_ENTER, 0, 1);
        int invalid = 0, submits = 0;
        while (weva_document_poll_event(f.doc, &event)) {
            if (event.kind == WEVA_EVENT_INVALID) ++invalid;
            if (event.kind == WEVA_EVENT_SUBMIT) ++submits;
        }
        CHECK(invalid == (row.bad && !bypass ? 1 : 0));
        CHECK(submits == (row.bad && !bypass ? 0 : 1));
    }
}

void test_abi_number_edit_history() {
    Form f("<input id=c type=number step=any>");
    const auto c = f.at("#c");
    weva_document_set_focus(f.doc, c);
    CHECK(weva_document_try_text_input(f.doc, "1e"));
    weva_document_update(f.doc, 0);
    int start = -1, end = -1;
    weva_element_selection(f.doc, c, &start, &end);
    CHECK(start == 2 && end == 2); CHECK(f.value("#c").empty());
    // The caret stays at the end of the visible raw text, so completing it
    // yields 1e2 instead of inserting at offset zero of the public empty value.
    CHECK(weva_document_try_text_input(f.doc, "2")); CHECK(f.value("#c") == "1e2");
    weva_document_key(f.doc, WEVA_KEY_BACKSPACE, 0, 1);
    CHECK(f.value("#c").empty());
    CHECK(weva_document_undo(f.doc)); CHECK(f.value("#c") == "1e2");
    CHECK(weva_document_redo(f.doc)); CHECK(f.value("#c").empty());
    CHECK(weva_document_set_composition(f.doc, "3", 0, 1));
    CHECK(f.value("#c") == "1e3");
    CHECK(weva_document_commit_composition(f.doc, ""));
    CHECK(f.value("#c").empty());
    weva_element_selection(f.doc, c, &start, &end); CHECK(end == 2);
    // Script assignment must clear a bad edit even when both public values
    // are empty, and move the caret into the new buffer.
    f.set("#c", ""); CHECK(f.value("#c").empty());
    weva_element_selection(f.doc, c, &start, &end); CHECK(end == 0);
    CHECK(weva_document_try_text_input(f.doc, "4")); CHECK(f.value("#c") == "4");
}

void test_form_number_filter() {
    struct Case { const char *initial, *place, *text, *raw, *value; bool bad; };
    const Case cases[] = {
        {"", "end", "abc", "", "", false},
        {"", "end", "1a2", "12", "12", false},
        {"", "end", "1 2", "12", "12", false},
        {"", "end", "1,2", "1,2", "1.2", false},
        {"", "end", "1..2", "1.2", "1.2", false},
        {"", "end", "1e2e3", "1e23", "1e23", false},
        {"", "end", "1e.2", "1e2", "1e2", false},
        {"", "end", "1.e2", "1.e2", "1.e2", false},
        {"", "end", "--1", "--1", "", true},
        {"", "end", "++1", "++1", "", true},
        {"", "end", "+-1", "+-1", "", true},
        {"", "end", "-+1", "-+1", "", true},
        {"", "end", "1-2", "1-2", "", true},
        {"", "end", "1+2", "1+2", "", true},
        {"", "end", "1e--2", "1e-2", "1e-2", false},
        {"", "end", "1e++2", "1e+2", "1e+2", false},
        {"", "end", "E2", "E2", "", true},
        {"", "end", "2E3", "2E3", "2E3", false},
        {"", "end", "1e999x", "1e999", "", true},
        {"", "end", "\u0661\u0662", "", "", false},
        {"", "end", "\uff11\uff12", "12", "12", false},
        {"", "end", "\u22121", "1", "1", false},
        {"", "end", "1\n2", "12", "12", false},
        {"", "end", "\uff0d\uff12", "-2", "-2", false},
        {"", "end", "\u30fc\uff12", "-2", "-2", false},
        {"", "end", "\uff11\uff0e\uff12", "1.2", "1.2", false},
        {"", "end", "\uff0b\uff11", "1", "1", false},
        {"", "end", "1e+-2", "1e+2", "1e+2", false},
        {"", "end", "1e-+2", "1e-2", "1e-2", false},
        {"", "start", "abc", "", "", false},
        {"", "start", "1a2", "12", "12", false},
        {"", "start", "1 2", "12", "12", false},
        {"", "start", "1,2", "1,2", "1.2", false},
        {"", "start", "1..2", "1.2", "1.2", false},
        {"", "start", "1e2e3", "1e23", "1e23", false},
        {"", "start", "1e.2", "1e2", "1e2", false},
        {"", "start", "1.e2", "1.e2", "1.e2", false},
        {"", "start", "--1", "--1", "", true},
        {"", "start", "++1", "++1", "", true},
        {"", "start", "+-1", "+-1", "", true},
        {"", "start", "-+1", "-+1", "", true},
        {"", "start", "1-2", "1-2", "", true},
        {"", "start", "1+2", "1+2", "", true},
        {"", "start", "1e--2", "1e-2", "1e-2", false},
        {"", "start", "1e++2", "1e+2", "1e+2", false},
        {"", "start", "E2", "E2", "", true},
        {"", "start", "2E3", "2E3", "2E3", false},
        {"", "start", "1e999x", "1e999", "", true},
        {"", "start", "\u0661\u0662", "", "", false},
        {"", "start", "\uff11\uff12", "12", "12", false},
        {"", "start", "\u22121", "1", "1", false},
        {"", "start", "1\n2", "12", "12", false},
        {"", "start", "\uff0d\uff12", "-2", "-2", false},
        {"", "start", "\u30fc\uff12", "-2", "-2", false},
        {"", "start", "\uff11\uff0e\uff12", "1.2", "1.2", false},
        {"", "start", "\uff0b\uff11", "1", "1", false},
        {"", "start", "1e+-2", "1e+2", "1e+2", false},
        {"", "start", "1e-+2", "1e-2", "1e-2", false},
        {"", "all", "abc", "", "", false},
        {"", "all", "1a2", "12", "12", false},
        {"", "all", "1 2", "12", "12", false},
        {"", "all", "1,2", "1,2", "1.2", false},
        {"", "all", "1..2", "1.2", "1.2", false},
        {"", "all", "1e2e3", "1e23", "1e23", false},
        {"", "all", "1e.2", "1e2", "1e2", false},
        {"", "all", "1.e2", "1.e2", "1.e2", false},
        {"", "all", "--1", "--1", "", true},
        {"", "all", "++1", "++1", "", true},
        {"", "all", "+-1", "+-1", "", true},
        {"", "all", "-+1", "-+1", "", true},
        {"", "all", "1-2", "1-2", "", true},
        {"", "all", "1+2", "1+2", "", true},
        {"", "all", "1e--2", "1e-2", "1e-2", false},
        {"", "all", "1e++2", "1e+2", "1e+2", false},
        {"", "all", "E2", "E2", "", true},
        {"", "all", "2E3", "2E3", "2E3", false},
        {"", "all", "1e999x", "1e999", "", true},
        {"", "all", "\u0661\u0662", "", "", false},
        {"", "all", "\uff11\uff12", "12", "12", false},
        {"", "all", "\u22121", "1", "1", false},
        {"", "all", "1\n2", "12", "12", false},
        {"", "all", "\uff0d\uff12", "-2", "-2", false},
        {"", "all", "\u30fc\uff12", "-2", "-2", false},
        {"", "all", "\uff11\uff0e\uff12", "1.2", "1.2", false},
        {"", "all", "\uff0b\uff11", "1", "1", false},
        {"", "all", "1e+-2", "1e+2", "1e+2", false},
        {"", "all", "1e-+2", "1e-2", "1e-2", false},
        {"", "middle", "abc", "", "", false},
        {"", "middle", "1a2", "12", "12", false},
        {"", "middle", "1 2", "12", "12", false},
        {"", "middle", "1,2", "1,2", "1.2", false},
        {"", "middle", "1..2", "1.2", "1.2", false},
        {"", "middle", "1e2e3", "1e23", "1e23", false},
        {"", "middle", "1e.2", "1e2", "1e2", false},
        {"", "middle", "1.e2", "1.e2", "1.e2", false},
        {"", "middle", "--1", "--1", "", true},
        {"", "middle", "++1", "++1", "", true},
        {"", "middle", "+-1", "+-1", "", true},
        {"", "middle", "-+1", "-+1", "", true},
        {"", "middle", "1-2", "1-2", "", true},
        {"", "middle", "1+2", "1+2", "", true},
        {"", "middle", "1e--2", "1e-2", "1e-2", false},
        {"", "middle", "1e++2", "1e+2", "1e+2", false},
        {"", "middle", "E2", "E2", "", true},
        {"", "middle", "2E3", "2E3", "2E3", false},
        {"", "middle", "1e999x", "1e999", "", true},
        {"", "middle", "\u0661\u0662", "", "", false},
        {"", "middle", "\uff11\uff12", "12", "12", false},
        {"", "middle", "\u22121", "1", "1", false},
        {"", "middle", "1\n2", "12", "12", false},
        {"", "middle", "\uff0d\uff12", "-2", "-2", false},
        {"", "middle", "\u30fc\uff12", "-2", "-2", false},
        {"", "middle", "\uff11\uff0e\uff12", "1.2", "1.2", false},
        {"", "middle", "\uff0b\uff11", "1", "1", false},
        {"", "middle", "1e+-2", "1e+2", "1e+2", false},
        {"", "middle", "1e-+2", "1e-2", "1e-2", false},
        {"12.3", "end", "abc", "12.3", "12.3", false},
        {"12.3", "end", "1a2", "12.312", "12.312", false},
        {"12.3", "end", "1 2", "12.312", "12.312", false},
        {"12.3", "end", "1,2", "12.312", "12.312", false},
        {"12.3", "end", "1..2", "12.312", "12.312", false},
        {"12.3", "end", "1e2e3", "12.31e23", "12.31e23", false},
        {"12.3", "end", "1e.2", "12.31e2", "12.31e2", false},
        {"12.3", "end", "1.e2", "12.31e2", "12.31e2", false},
        {"12.3", "end", "--1", "12.3--1", "", true},
        {"12.3", "end", "++1", "12.3++1", "", true},
        {"12.3", "end", "+-1", "12.3+-1", "", true},
        {"12.3", "end", "-+1", "12.3-+1", "", true},
        {"12.3", "end", "1-2", "12.31-2", "", true},
        {"12.3", "end", "1+2", "12.31+2", "", true},
        {"12.3", "end", "1e--2", "12.31e-2", "12.31e-2", false},
        {"12.3", "end", "1e++2", "12.31e+2", "12.31e+2", false},
        {"12.3", "end", "E2", "12.3E2", "12.3E2", false},
        {"12.3", "end", "2E3", "12.32E3", "12.32E3", false},
        {"12.3", "end", "1e999x", "12.31e999", "", true},
        {"12.3", "end", "\u0661\u0662", "12.3", "12.3", false},
        {"12.3", "end", "\uff11\uff12", "12.312", "12.312", false},
        {"12.3", "end", "\u22121", "12.31", "12.31", false},
        {"12.3", "end", "1\n2", "12.312", "12.312", false},
        {"12.3", "end", "\uff0d\uff12", "12.3-2", "", true},
        {"12.3", "end", "\u30fc\uff12", "12.3-2", "", true},
        {"12.3", "end", "\uff11\uff0e\uff12", "12.312", "12.312", false},
        {"12.3", "end", "\uff0b\uff11", "12.31", "12.31", false},
        {"12.3", "end", "1e+-2", "12.31e+2", "12.31e+2", false},
        {"12.3", "end", "1e-+2", "12.31e-2", "12.31e-2", false},
        {"12.3", "start", "abc", "12.3", "12.3", false},
        {"12.3", "start", "1a2", "1212.3", "1212.3", false},
        {"12.3", "start", "1 2", "1212.3", "1212.3", false},
        {"12.3", "start", "1,2", "1212.3", "1212.3", false},
        {"12.3", "start", "1..2", "1212.3", "1212.3", false},
        {"12.3", "start", "1e2e3", "12312.3", "12312.3", false},
        {"12.3", "start", "1e.2", "1212.3", "1212.3", false},
        {"12.3", "start", "1.e2", "1212.3", "1212.3", false},
        {"12.3", "start", "--1", "--112.3", "", true},
        {"12.3", "start", "++1", "++112.3", "", true},
        {"12.3", "start", "+-1", "+-112.3", "", true},
        {"12.3", "start", "-+1", "-+112.3", "", true},
        {"12.3", "start", "1-2", "1-212.3", "", true},
        {"12.3", "start", "1+2", "1+212.3", "", true},
        {"12.3", "start", "1e--2", "1--212.3", "", true},
        {"12.3", "start", "1e++2", "1++212.3", "", true},
        {"12.3", "start", "E2", "212.3", "212.3", false},
        {"12.3", "start", "2E3", "2312.3", "2312.3", false},
        {"12.3", "start", "1e999x", "199912.3", "199912.3", false},
        {"12.3", "start", "\u0661\u0662", "12.3", "12.3", false},
        {"12.3", "start", "\uff11\uff12", "1212.3", "1212.3", false},
        {"12.3", "start", "\u22121", "112.3", "112.3", false},
        {"12.3", "start", "1\n2", "1212.3", "1212.3", false},
        {"12.3", "start", "\uff0d\uff12", "-212.3", "-212.3", false},
        {"12.3", "start", "\u30fc\uff12", "-212.3", "-212.3", false},
        {"12.3", "start", "\uff11\uff0e\uff12", "1212.3", "1212.3", false},
        {"12.3", "start", "\uff0b\uff11", "112.3", "112.3", false},
        {"12.3", "start", "1e+-2", "1+-212.3", "", true},
        {"12.3", "start", "1e-+2", "1-+212.3", "", true},
        {"12.3", "all", "abc", "", "", false},
        {"12.3", "all", "1a2", "12", "12", false},
        {"12.3", "all", "1 2", "12", "12", false},
        {"12.3", "all", "1,2", "1,2", "1.2", false},
        {"12.3", "all", "1..2", "1.2", "1.2", false},
        {"12.3", "all", "1e2e3", "1e23", "1e23", false},
        {"12.3", "all", "1e.2", "1e2", "1e2", false},
        {"12.3", "all", "1.e2", "1.e2", "1.e2", false},
        {"12.3", "all", "--1", "--1", "", true},
        {"12.3", "all", "++1", "++1", "", true},
        {"12.3", "all", "+-1", "+-1", "", true},
        {"12.3", "all", "-+1", "-+1", "", true},
        {"12.3", "all", "1-2", "1-2", "", true},
        {"12.3", "all", "1+2", "1+2", "", true},
        {"12.3", "all", "1e--2", "1e-2", "1e-2", false},
        {"12.3", "all", "1e++2", "1e+2", "1e+2", false},
        {"12.3", "all", "E2", "E2", "", true},
        {"12.3", "all", "2E3", "2E3", "2E3", false},
        {"12.3", "all", "1e999x", "1e999", "", true},
        {"12.3", "all", "\u0661\u0662", "", "", false},
        {"12.3", "all", "\uff11\uff12", "12", "12", false},
        {"12.3", "all", "\u22121", "1", "1", false},
        {"12.3", "all", "1\n2", "12", "12", false},
        {"12.3", "all", "\uff0d\uff12", "-2", "-2", false},
        {"12.3", "all", "\u30fc\uff12", "-2", "-2", false},
        {"12.3", "all", "\uff11\uff0e\uff12", "1.2", "1.2", false},
        {"12.3", "all", "\uff0b\uff11", "1", "1", false},
        {"12.3", "all", "1e+-2", "1e+2", "1e+2", false},
        {"12.3", "all", "1e-+2", "1e-2", "1e-2", false},
        {"12.3", "middle", "abc", "12.3", "12.3", false},
        {"12.3", "middle", "1a2", "1212.3", "1212.3", false},
        {"12.3", "middle", "1 2", "1212.3", "1212.3", false},
        {"12.3", "middle", "1,2", "1212.3", "1212.3", false},
        {"12.3", "middle", "1..2", "1212.3", "1212.3", false},
        {"12.3", "middle", "1e2e3", "12123.3", "12123.3", false},
        {"12.3", "middle", "1e.2", "1212.3", "1212.3", false},
        {"12.3", "middle", "1.e2", "1212.3", "1212.3", false},
        {"12.3", "middle", "--1", "12--1.3", "", true},
        {"12.3", "middle", "++1", "12++1.3", "", true},
        {"12.3", "middle", "+-1", "12+-1.3", "", true},
        {"12.3", "middle", "-+1", "12-+1.3", "", true},
        {"12.3", "middle", "1-2", "121-2.3", "", true},
        {"12.3", "middle", "1+2", "121+2.3", "", true},
        {"12.3", "middle", "1e--2", "121--2.3", "", true},
        {"12.3", "middle", "1e++2", "121++2.3", "", true},
        {"12.3", "middle", "E2", "122.3", "122.3", false},
        {"12.3", "middle", "2E3", "1223.3", "1223.3", false},
        {"12.3", "middle", "1e999x", "121999.3", "121999.3", false},
        {"12.3", "middle", "\u0661\u0662", "12.3", "12.3", false},
        {"12.3", "middle", "\uff11\uff12", "1212.3", "1212.3", false},
        {"12.3", "middle", "\u22121", "121.3", "121.3", false},
        {"12.3", "middle", "1\n2", "1212.3", "1212.3", false},
        {"12.3", "middle", "\uff0d\uff12", "12-2.3", "", true},
        {"12.3", "middle", "\u30fc\uff12", "12-2.3", "", true},
        {"12.3", "middle", "\uff11\uff0e\uff12", "1212.3", "1212.3", false},
        {"12.3", "middle", "\uff0b\uff11", "121.3", "121.3", false},
        {"12.3", "middle", "1e+-2", "121+-2.3", "", true},
        {"12.3", "middle", "1e-+2", "121-+2.3", "", true},
        {"1e2", "end", "abc", "1e2", "1e2", false},
        {"1e2", "end", "1a2", "1e212", "1e212", false},
        {"1e2", "end", "1 2", "1e212", "1e212", false},
        {"1e2", "end", "1,2", "1e212", "1e212", false},
        {"1e2", "end", "1..2", "1e212", "1e212", false},
        {"1e2", "end", "1e2e3", "1e2123", "", true},
        {"1e2", "end", "1e.2", "1e212", "1e212", false},
        {"1e2", "end", "1.e2", "1e212", "1e212", false},
        {"1e2", "end", "--1", "1e21", "1e21", false},
        {"1e2", "end", "++1", "1e21", "1e21", false},
        {"1e2", "end", "+-1", "1e21", "1e21", false},
        {"1e2", "end", "-+1", "1e21", "1e21", false},
        {"1e2", "end", "1-2", "1e212", "1e212", false},
        {"1e2", "end", "1+2", "1e212", "1e212", false},
        {"1e2", "end", "1e--2", "1e212", "1e212", false},
        {"1e2", "end", "1e++2", "1e212", "1e212", false},
        {"1e2", "end", "E2", "1e22", "1e22", false},
        {"1e2", "end", "2E3", "1e223", "1e223", false},
        {"1e2", "end", "1e999x", "1e21999", "", true},
        {"1e2", "end", "\u0661\u0662", "1e2", "1e2", false},
        {"1e2", "end", "\uff11\uff12", "1e212", "1e212", false},
        {"1e2", "end", "\u22121", "1e21", "1e21", false},
        {"1e2", "end", "1\n2", "1e212", "1e212", false},
        {"1e2", "end", "\uff0d\uff12", "1e22", "1e22", false},
        {"1e2", "end", "\u30fc\uff12", "1e22", "1e22", false},
        {"1e2", "end", "\uff11\uff0e\uff12", "1e212", "1e212", false},
        {"1e2", "end", "\uff0b\uff11", "1e21", "1e21", false},
        {"1e2", "end", "1e+-2", "1e212", "1e212", false},
        {"1e2", "end", "1e-+2", "1e212", "1e212", false},
        {"1e2", "start", "abc", "1e2", "1e2", false},
        {"1e2", "start", "1a2", "121e2", "121e2", false},
        {"1e2", "start", "1 2", "121e2", "121e2", false},
        {"1e2", "start", "1,2", "1,21e2", "", true},
        {"1e2", "start", "1..2", "1.21e2", "1.21e2", false},
        {"1e2", "start", "1e2e3", "1231e2", "1231e2", false},
        {"1e2", "start", "1e.2", "1.21e2", "1.21e2", false},
        {"1e2", "start", "1.e2", "1.21e2", "1.21e2", false},
        {"1e2", "start", "--1", "-11e2", "-11e2", false},
        {"1e2", "start", "++1", "11e2", "11e2", false},
        {"1e2", "start", "+-1", "-11e2", "-11e2", false},
        {"1e2", "start", "-+1", "-11e2", "-11e2", false},
        {"1e2", "start", "1-2", "121e2", "121e2", false},
        {"1e2", "start", "1+2", "121e2", "121e2", false},
        {"1e2", "start", "1e--2", "121e2", "121e2", false},
        {"1e2", "start", "1e++2", "121e2", "121e2", false},
        {"1e2", "start", "E2", "21e2", "21e2", false},
        {"1e2", "start", "2E3", "231e2", "231e2", false},
        {"1e2", "start", "1e999x", "19991e2", "19991e2", false},
        {"1e2", "start", "\u0661\u0662", "1e2", "1e2", false},
        {"1e2", "start", "\uff11\uff12", "121e2", "121e2", false},
        {"1e2", "start", "\u22121", "11e2", "11e2", false},
        {"1e2", "start", "1\n2", "121e2", "121e2", false},
        {"1e2", "start", "\uff0d\uff12", "-21e2", "-21e2", false},
        {"1e2", "start", "\u30fc\uff12", "-21e2", "-21e2", false},
        {"1e2", "start", "\uff11\uff0e\uff12", "1.21e2", "1.21e2", false},
        {"1e2", "start", "\uff0b\uff11", "11e2", "11e2", false},
        {"1e2", "start", "1e+-2", "121e2", "121e2", false},
        {"1e2", "start", "1e-+2", "121e2", "121e2", false},
        {"1e2", "all", "abc", "", "", false},
        {"1e2", "all", "1a2", "12", "12", false},
        {"1e2", "all", "1 2", "12", "12", false},
        {"1e2", "all", "1,2", "1,2", "1.2", false},
        {"1e2", "all", "1..2", "1.2", "1.2", false},
        {"1e2", "all", "1e2e3", "1e23", "1e23", false},
        {"1e2", "all", "1e.2", "1e2", "1e2", false},
        {"1e2", "all", "1.e2", "1.e2", "1.e2", false},
        {"1e2", "all", "--1", "--1", "", true},
        {"1e2", "all", "++1", "++1", "", true},
        {"1e2", "all", "+-1", "+-1", "", true},
        {"1e2", "all", "-+1", "-+1", "", true},
        {"1e2", "all", "1-2", "1-2", "", true},
        {"1e2", "all", "1+2", "1+2", "", true},
        {"1e2", "all", "1e--2", "1e-2", "1e-2", false},
        {"1e2", "all", "1e++2", "1e+2", "1e+2", false},
        {"1e2", "all", "E2", "E2", "", true},
        {"1e2", "all", "2E3", "2E3", "2E3", false},
        {"1e2", "all", "1e999x", "1e999", "", true},
        {"1e2", "all", "\u0661\u0662", "", "", false},
        {"1e2", "all", "\uff11\uff12", "12", "12", false},
        {"1e2", "all", "\u22121", "1", "1", false},
        {"1e2", "all", "1\n2", "12", "12", false},
        {"1e2", "all", "\uff0d\uff12", "-2", "-2", false},
        {"1e2", "all", "\u30fc\uff12", "-2", "-2", false},
        {"1e2", "all", "\uff11\uff0e\uff12", "1.2", "1.2", false},
        {"1e2", "all", "\uff0b\uff11", "1", "1", false},
        {"1e2", "all", "1e+-2", "1e+2", "1e+2", false},
        {"1e2", "all", "1e-+2", "1e-2", "1e-2", false},
        {"1e2", "middle", "abc", "1e2", "1e2", false},
        {"1e2", "middle", "1a2", "1e122", "1e122", false},
        {"1e2", "middle", "1 2", "1e122", "1e122", false},
        {"1e2", "middle", "1,2", "1e122", "1e122", false},
        {"1e2", "middle", "1..2", "1e122", "1e122", false},
        {"1e2", "middle", "1e2e3", "1e1232", "", true},
        {"1e2", "middle", "1e.2", "1e122", "1e122", false},
        {"1e2", "middle", "1.e2", "1e122", "1e122", false},
        {"1e2", "middle", "--1", "1e-12", "1e-12", false},
        {"1e2", "middle", "++1", "1e+12", "1e+12", false},
        {"1e2", "middle", "+-1", "1e+12", "1e+12", false},
        {"1e2", "middle", "-+1", "1e-12", "1e-12", false},
        {"1e2", "middle", "1-2", "1e122", "1e122", false},
        {"1e2", "middle", "1+2", "1e122", "1e122", false},
        {"1e2", "middle", "1e--2", "1e122", "1e122", false},
        {"1e2", "middle", "1e++2", "1e122", "1e122", false},
        {"1e2", "middle", "E2", "1e22", "1e22", false},
        {"1e2", "middle", "2E3", "1e232", "1e232", false},
        {"1e2", "middle", "1e999x", "1e19992", "", true},
        {"1e2", "middle", "\u0661\u0662", "1e2", "1e2", false},
        {"1e2", "middle", "\uff11\uff12", "1e122", "1e122", false},
        {"1e2", "middle", "\u22121", "1e12", "1e12", false},
        {"1e2", "middle", "1\n2", "1e122", "1e122", false},
        {"1e2", "middle", "\uff0d\uff12", "1e-22", "1e-22", false},
        {"1e2", "middle", "\u30fc\uff12", "1e-22", "1e-22", false},
        {"1e2", "middle", "\uff11\uff0e\uff12", "1e122", "1e122", false},
        {"1e2", "middle", "\uff0b\uff11", "1e12", "1e12", false},
        {"1e2", "middle", "1e+-2", "1e122", "1e122", false},
        {"1e2", "middle", "1e-+2", "1e122", "1e122", false},
        {"-12", "end", "abc", "-12", "-12", false},
        {"-12", "end", "1a2", "-1212", "-1212", false},
        {"-12", "end", "1 2", "-1212", "-1212", false},
        {"-12", "end", "1,2", "-121,2", "-121.2", false},
        {"-12", "end", "1..2", "-121.2", "-121.2", false},
        {"-12", "end", "1e2e3", "-121e23", "-121e23", false},
        {"-12", "end", "1e.2", "-121e2", "-121e2", false},
        {"-12", "end", "1.e2", "-121.e2", "-121.e2", false},
        {"-12", "end", "--1", "-12-1", "", true},
        {"-12", "end", "++1", "-12+1", "", true},
        {"-12", "end", "+-1", "-12+1", "", true},
        {"-12", "end", "-+1", "-12-1", "", true},
        {"-12", "end", "1-2", "-121-2", "", true},
        {"-12", "end", "1+2", "-121+2", "", true},
        {"-12", "end", "1e--2", "-121e-2", "-121e-2", false},
        {"-12", "end", "1e++2", "-121e+2", "-121e+2", false},
        {"-12", "end", "E2", "-12E2", "-12E2", false},
        {"-12", "end", "2E3", "-122E3", "-122E3", false},
        {"-12", "end", "1e999x", "-121e999", "", true},
        {"-12", "end", "\u0661\u0662", "-12", "-12", false},
        {"-12", "end", "\uff11\uff12", "-1212", "-1212", false},
        {"-12", "end", "\u22121", "-121", "-121", false},
        {"-12", "end", "1\n2", "-1212", "-1212", false},
        {"-12", "end", "\uff0d\uff12", "-12-2", "", true},
        {"-12", "end", "\u30fc\uff12", "-12-2", "", true},
        {"-12", "end", "\uff11\uff0e\uff12", "-121.2", "-121.2", false},
        {"-12", "end", "\uff0b\uff11", "-121", "-121", false},
        {"-12", "end", "1e+-2", "-121e+2", "-121e+2", false},
        {"-12", "end", "1e-+2", "-121e-2", "-121e-2", false},
        {"-12", "start", "abc", "-12", "-12", false},
        {"-12", "start", "1a2", "-12", "-12", false},
        {"-12", "start", "1 2", "-12", "-12", false},
        {"-12", "start", "1,2", "-12", "-12", false},
        {"-12", "start", "1..2", "-12", "-12", false},
        {"-12", "start", "1e2e3", "e-12", "", true},
        {"-12", "start", "1e.2", "e-12", "", true},
        {"-12", "start", "1.e2", "e-12", "", true},
        {"-12", "start", "--1", "-12", "-12", false},
        {"-12", "start", "++1", "-12", "-12", false},
        {"-12", "start", "+-1", "-12", "-12", false},
        {"-12", "start", "-+1", "-12", "-12", false},
        {"-12", "start", "1-2", "-12", "-12", false},
        {"-12", "start", "1+2", "-12", "-12", false},
        {"-12", "start", "1e--2", "e-12", "", true},
        {"-12", "start", "1e++2", "e-12", "", true},
        {"-12", "start", "E2", "E-12", "", true},
        {"-12", "start", "2E3", "E-12", "", true},
        {"-12", "start", "1e999x", "e-12", "", true},
        {"-12", "start", "\u0661\u0662", "-12", "-12", false},
        {"-12", "start", "\uff11\uff12", "-12", "-12", false},
        {"-12", "start", "\u22121", "-12", "-12", false},
        {"-12", "start", "1\n2", "-12", "-12", false},
        {"-12", "start", "\uff0d\uff12", "-12", "-12", false},
        {"-12", "start", "\u30fc\uff12", "-12", "-12", false},
        {"-12", "start", "\uff11\uff0e\uff12", "-12", "-12", false},
        {"-12", "start", "\uff0b\uff11", "-12", "-12", false},
        {"-12", "start", "1e+-2", "e-12", "", true},
        {"-12", "start", "1e-+2", "e-12", "", true},
        {"-12", "all", "abc", "", "", false},
        {"-12", "all", "1a2", "12", "12", false},
        {"-12", "all", "1 2", "12", "12", false},
        {"-12", "all", "1,2", "1,2", "1.2", false},
        {"-12", "all", "1..2", "1.2", "1.2", false},
        {"-12", "all", "1e2e3", "1e23", "1e23", false},
        {"-12", "all", "1e.2", "1e2", "1e2", false},
        {"-12", "all", "1.e2", "1.e2", "1.e2", false},
        {"-12", "all", "--1", "--1", "", true},
        {"-12", "all", "++1", "++1", "", true},
        {"-12", "all", "+-1", "+-1", "", true},
        {"-12", "all", "-+1", "-+1", "", true},
        {"-12", "all", "1-2", "1-2", "", true},
        {"-12", "all", "1+2", "1+2", "", true},
        {"-12", "all", "1e--2", "1e-2", "1e-2", false},
        {"-12", "all", "1e++2", "1e+2", "1e+2", false},
        {"-12", "all", "E2", "E2", "", true},
        {"-12", "all", "2E3", "2E3", "2E3", false},
        {"-12", "all", "1e999x", "1e999", "", true},
        {"-12", "all", "\u0661\u0662", "", "", false},
        {"-12", "all", "\uff11\uff12", "12", "12", false},
        {"-12", "all", "\u22121", "1", "1", false},
        {"-12", "all", "1\n2", "12", "12", false},
        {"-12", "all", "\uff0d\uff12", "-2", "-2", false},
        {"-12", "all", "\u30fc\uff12", "-2", "-2", false},
        {"-12", "all", "\uff11\uff0e\uff12", "1.2", "1.2", false},
        {"-12", "all", "\uff0b\uff11", "1", "1", false},
        {"-12", "all", "1e+-2", "1e+2", "1e+2", false},
        {"-12", "all", "1e-+2", "1e-2", "1e-2", false},
        {"-12", "middle", "abc", "-12", "-12", false},
        {"-12", "middle", "1a2", "-1122", "-1122", false},
        {"-12", "middle", "1 2", "-1122", "-1122", false},
        {"-12", "middle", "1,2", "-11,22", "-11.22", false},
        {"-12", "middle", "1..2", "-11.22", "-11.22", false},
        {"-12", "middle", "1e2e3", "-11e232", "-11e232", false},
        {"-12", "middle", "1e.2", "-11e22", "-11e22", false},
        {"-12", "middle", "1.e2", "-11.e22", "-11.e22", false},
        {"-12", "middle", "--1", "-1-12", "", true},
        {"-12", "middle", "++1", "-1+12", "", true},
        {"-12", "middle", "+-1", "-1+12", "", true},
        {"-12", "middle", "-+1", "-1-12", "", true},
        {"-12", "middle", "1-2", "-11-22", "", true},
        {"-12", "middle", "1+2", "-11+22", "", true},
        {"-12", "middle", "1e--2", "-11e-22", "-11e-22", false},
        {"-12", "middle", "1e++2", "-11e+22", "-11e+22", false},
        {"-12", "middle", "E2", "-1E22", "-1E22", false},
        {"-12", "middle", "2E3", "-12E32", "-12E32", false},
        {"-12", "middle", "1e999x", "-11e9992", "", true},
        {"-12", "middle", "\u0661\u0662", "-12", "-12", false},
        {"-12", "middle", "\uff11\uff12", "-1122", "-1122", false},
        {"-12", "middle", "\u22121", "-112", "-112", false},
        {"-12", "middle", "1\n2", "-1122", "-1122", false},
        {"-12", "middle", "\uff0d\uff12", "-1-22", "", true},
        {"-12", "middle", "\u30fc\uff12", "-1-22", "", true},
        {"-12", "middle", "\uff11\uff0e\uff12", "-11.22", "-11.22", false},
        {"-12", "middle", "\uff0b\uff11", "-112", "-112", false},
        {"-12", "middle", "1e+-2", "-11e+22", "-11e+22", false},
        {"-12", "middle", "1e-+2", "-11e-22", "-11e-22", false},
        {"1e-2", "end", "abc", "1e-2", "1e-2", false},
        {"1e-2", "end", "1a2", "1e-212", "1e-212", false},
        {"1e-2", "end", "1 2", "1e-212", "1e-212", false},
        {"1e-2", "end", "1,2", "1e-212", "1e-212", false},
        {"1e-2", "end", "1..2", "1e-212", "1e-212", false},
        {"1e-2", "end", "1e2e3", "1e-2123", "1e-2123", false},
        {"1e-2", "end", "1e.2", "1e-212", "1e-212", false},
        {"1e-2", "end", "1.e2", "1e-212", "1e-212", false},
        {"1e-2", "end", "--1", "1e-21", "1e-21", false},
        {"1e-2", "end", "++1", "1e-21", "1e-21", false},
        {"1e-2", "end", "+-1", "1e-21", "1e-21", false},
        {"1e-2", "end", "-+1", "1e-21", "1e-21", false},
        {"1e-2", "end", "1-2", "1e-212", "1e-212", false},
        {"1e-2", "end", "1+2", "1e-212", "1e-212", false},
        {"1e-2", "end", "1e--2", "1e-212", "1e-212", false},
        {"1e-2", "end", "1e++2", "1e-212", "1e-212", false},
        {"1e-2", "end", "E2", "1e-22", "1e-22", false},
        {"1e-2", "end", "2E3", "1e-223", "1e-223", false},
        {"1e-2", "end", "1e999x", "1e-21999", "1e-21999", false},
        {"1e-2", "end", "\u0661\u0662", "1e-2", "1e-2", false},
        {"1e-2", "end", "\uff11\uff12", "1e-212", "1e-212", false},
        {"1e-2", "end", "\u22121", "1e-21", "1e-21", false},
        {"1e-2", "end", "1\n2", "1e-212", "1e-212", false},
        {"1e-2", "end", "\uff0d\uff12", "1e-22", "1e-22", false},
        {"1e-2", "end", "\u30fc\uff12", "1e-22", "1e-22", false},
        {"1e-2", "end", "\uff11\uff0e\uff12", "1e-212", "1e-212", false},
        {"1e-2", "end", "\uff0b\uff11", "1e-21", "1e-21", false},
        {"1e-2", "end", "1e+-2", "1e-212", "1e-212", false},
        {"1e-2", "end", "1e-+2", "1e-212", "1e-212", false},
        {"1e-2", "start", "abc", "1e-2", "1e-2", false},
        {"1e-2", "start", "1a2", "121e-2", "121e-2", false},
        {"1e-2", "start", "1 2", "121e-2", "121e-2", false},
        {"1e-2", "start", "1,2", "1,21e-2", "", true},
        {"1e-2", "start", "1..2", "1.21e-2", "1.21e-2", false},
        {"1e-2", "start", "1e2e3", "1231e-2", "1231e-2", false},
        {"1e-2", "start", "1e.2", "1.21e-2", "1.21e-2", false},
        {"1e-2", "start", "1.e2", "1.21e-2", "1.21e-2", false},
        {"1e-2", "start", "--1", "-11e-2", "-11e-2", false},
        {"1e-2", "start", "++1", "11e-2", "11e-2", false},
        {"1e-2", "start", "+-1", "-11e-2", "-11e-2", false},
        {"1e-2", "start", "-+1", "-11e-2", "-11e-2", false},
        {"1e-2", "start", "1-2", "121e-2", "121e-2", false},
        {"1e-2", "start", "1+2", "121e-2", "121e-2", false},
        {"1e-2", "start", "1e--2", "121e-2", "121e-2", false},
        {"1e-2", "start", "1e++2", "121e-2", "121e-2", false},
        {"1e-2", "start", "E2", "21e-2", "21e-2", false},
        {"1e-2", "start", "2E3", "231e-2", "231e-2", false},
        {"1e-2", "start", "1e999x", "19991e-2", "19991e-2", false},
        {"1e-2", "start", "\u0661\u0662", "1e-2", "1e-2", false},
        {"1e-2", "start", "\uff11\uff12", "121e-2", "121e-2", false},
        {"1e-2", "start", "\u22121", "11e-2", "11e-2", false},
        {"1e-2", "start", "1\n2", "121e-2", "121e-2", false},
        {"1e-2", "start", "\uff0d\uff12", "-21e-2", "-21e-2", false},
        {"1e-2", "start", "\u30fc\uff12", "-21e-2", "-21e-2", false},
        {"1e-2", "start", "\uff11\uff0e\uff12", "1.21e-2", "1.21e-2", false},
        {"1e-2", "start", "\uff0b\uff11", "11e-2", "11e-2", false},
        {"1e-2", "start", "1e+-2", "121e-2", "121e-2", false},
        {"1e-2", "start", "1e-+2", "121e-2", "121e-2", false},
        {"1e-2", "all", "abc", "", "", false},
        {"1e-2", "all", "1a2", "12", "12", false},
        {"1e-2", "all", "1 2", "12", "12", false},
        {"1e-2", "all", "1,2", "1,2", "1.2", false},
        {"1e-2", "all", "1..2", "1.2", "1.2", false},
        {"1e-2", "all", "1e2e3", "1e23", "1e23", false},
        {"1e-2", "all", "1e.2", "1e2", "1e2", false},
        {"1e-2", "all", "1.e2", "1.e2", "1.e2", false},
        {"1e-2", "all", "--1", "--1", "", true},
        {"1e-2", "all", "++1", "++1", "", true},
        {"1e-2", "all", "+-1", "+-1", "", true},
        {"1e-2", "all", "-+1", "-+1", "", true},
        {"1e-2", "all", "1-2", "1-2", "", true},
        {"1e-2", "all", "1+2", "1+2", "", true},
        {"1e-2", "all", "1e--2", "1e-2", "1e-2", false},
        {"1e-2", "all", "1e++2", "1e+2", "1e+2", false},
        {"1e-2", "all", "E2", "E2", "", true},
        {"1e-2", "all", "2E3", "2E3", "2E3", false},
        {"1e-2", "all", "1e999x", "1e999", "", true},
        {"1e-2", "all", "\u0661\u0662", "", "", false},
        {"1e-2", "all", "\uff11\uff12", "12", "12", false},
        {"1e-2", "all", "\u22121", "1", "1", false},
        {"1e-2", "all", "1\n2", "12", "12", false},
        {"1e-2", "all", "\uff0d\uff12", "-2", "-2", false},
        {"1e-2", "all", "\u30fc\uff12", "-2", "-2", false},
        {"1e-2", "all", "\uff11\uff0e\uff12", "1.2", "1.2", false},
        {"1e-2", "all", "\uff0b\uff11", "1", "1", false},
        {"1e-2", "all", "1e+-2", "1e+2", "1e+2", false},
        {"1e-2", "all", "1e-+2", "1e-2", "1e-2", false},
        {"1e-2", "middle", "abc", "1e-2", "1e-2", false},
        {"1e-2", "middle", "1a2", "1e-2", "1e-2", false},
        {"1e-2", "middle", "1 2", "1e-2", "1e-2", false},
        {"1e-2", "middle", "1,2", "1e-2", "1e-2", false},
        {"1e-2", "middle", "1..2", "1e-2", "1e-2", false},
        {"1e-2", "middle", "1e2e3", "1e-2", "1e-2", false},
        {"1e-2", "middle", "1e.2", "1e-2", "1e-2", false},
        {"1e-2", "middle", "1.e2", "1e-2", "1e-2", false},
        {"1e-2", "middle", "--1", "1e-2", "1e-2", false},
        {"1e-2", "middle", "++1", "1e-2", "1e-2", false},
        {"1e-2", "middle", "+-1", "1e-2", "1e-2", false},
        {"1e-2", "middle", "-+1", "1e-2", "1e-2", false},
        {"1e-2", "middle", "1-2", "1e-2", "1e-2", false},
        {"1e-2", "middle", "1+2", "1e-2", "1e-2", false},
        {"1e-2", "middle", "1e--2", "1e-2", "1e-2", false},
        {"1e-2", "middle", "1e++2", "1e-2", "1e-2", false},
        {"1e-2", "middle", "E2", "1e-2", "1e-2", false},
        {"1e-2", "middle", "2E3", "1e-2", "1e-2", false},
        {"1e-2", "middle", "1e999x", "1e-2", "1e-2", false},
        {"1e-2", "middle", "\u0661\u0662", "1e-2", "1e-2", false},
        {"1e-2", "middle", "\uff11\uff12", "1e-2", "1e-2", false},
        {"1e-2", "middle", "\u22121", "1e-2", "1e-2", false},
        {"1e-2", "middle", "1\n2", "1e-2", "1e-2", false},
        {"1e-2", "middle", "\uff0d\uff12", "1e-2", "1e-2", false},
        {"1e-2", "middle", "\u30fc\uff12", "1e-2", "1e-2", false},
        {"1e-2", "middle", "\uff11\uff0e\uff12", "1e-2", "1e-2", false},
        {"1e-2", "middle", "\uff0b\uff11", "1e-2", "1e-2", false},
        {"1e-2", "middle", "1e+-2", "1e-2", "1e-2", false},
        {"1e-2", "middle", "1e-+2", "1e-2", "1e-2", false},
    };
    for (const auto& row : cases) {
        const std::string initial(row.initial);
        const bool all = std::string_view(row.place) == "all";
        const size_t from = std::string_view(row.place) == "end" ? initial.size() :
            std::string_view(row.place) == "middle" ? std::min(size_t(2), initial.size()) : 0;
        const size_t to = all ? initial.size() : from;
        std::string scratch;
        const auto accepted = form_number_edit_text(initial, from, to, row.text, scratch);
        std::string raw = initial;
        raw.replace(from, to - from, accepted);
        if (raw != row.raw) std::fprintf(stderr, "Number filter: %s / %s / %s => %s expected %s\n", row.initial, row.place, row.text, raw.c_str(), row.raw);
        CHECK(raw == row.raw);
        auto e = make_ref<Element>("input"); e->set_attribute("type", "number");
        e->set_form_value(raw, false, true);
        CHECK(e->form_value() == row.value); CHECK(e->form_bad_input() == row.bad);
        Form f("<input id=c type=number step=any>");
        f.set("#c", row.initial);
        weva_element_set_selection(f.doc, f.at("#c"), static_cast<int>(from), static_cast<int>(to));
        CHECK(weva_document_try_text_input(f.doc, row.text));
        CHECK(f.value("#c") == row.value);
    }
}

void test_range_selectors_chrome() {
    const struct { const char* html; bool inside, outside; } rows[] = {
        {"<input id=c type=\"number\" value=\"\" step=\"any\" min=\"2\" max=\"8\">", true, false},
        {"<input id=c type=\"number\" value=\"\" step=\"any\">", true, false},
        {"<input id=c type=\"number\" value=\"\" step=\"any\" min=\"bad\" max=\"bad\">", true, false},
        {"<input id=c type=\"number\" value=\"\" step=\"any\" min=\"2\" max=\"8\" disabled=\"\">", false, false},
        {"<input id=c type=\"number\" value=\"\" step=\"any\" min=\"2\" max=\"8\" readonly=\"\">", false, false},
        {"<input id=c type=\"number\" value=\"1\" step=\"any\" min=\"2\" max=\"8\">", false, true},
        {"<input id=c type=\"number\" value=\"1\" step=\"any\">", false, false},
        {"<input id=c type=\"number\" value=\"1\" step=\"any\" min=\"bad\" max=\"bad\">", false, false},
        {"<input id=c type=\"number\" value=\"1\" step=\"any\" min=\"2\" max=\"8\" disabled=\"\">", false, false},
        {"<input id=c type=\"number\" value=\"1\" step=\"any\" min=\"2\" max=\"8\" readonly=\"\">", false, false},
        {"<input id=c type=\"number\" value=\"2\" step=\"any\" min=\"2\" max=\"8\">", true, false},
        {"<input id=c type=\"number\" value=\"2\" step=\"any\">", false, false},
        {"<input id=c type=\"number\" value=\"2\" step=\"any\" min=\"bad\" max=\"bad\">", false, false},
        {"<input id=c type=\"number\" value=\"2\" step=\"any\" min=\"2\" max=\"8\" disabled=\"\">", false, false},
        {"<input id=c type=\"number\" value=\"2\" step=\"any\" min=\"2\" max=\"8\" readonly=\"\">", false, false},
        {"<input id=c type=\"number\" value=\"5\" step=\"any\" min=\"2\" max=\"8\">", true, false},
        {"<input id=c type=\"number\" value=\"5\" step=\"any\">", false, false},
        {"<input id=c type=\"number\" value=\"5\" step=\"any\" min=\"bad\" max=\"bad\">", false, false},
        {"<input id=c type=\"number\" value=\"5\" step=\"any\" min=\"2\" max=\"8\" disabled=\"\">", false, false},
        {"<input id=c type=\"number\" value=\"5\" step=\"any\" min=\"2\" max=\"8\" readonly=\"\">", false, false},
        {"<input id=c type=\"number\" value=\"8\" step=\"any\" min=\"2\" max=\"8\">", true, false},
        {"<input id=c type=\"number\" value=\"8\" step=\"any\">", false, false},
        {"<input id=c type=\"number\" value=\"8\" step=\"any\" min=\"bad\" max=\"bad\">", false, false},
        {"<input id=c type=\"number\" value=\"8\" step=\"any\" min=\"2\" max=\"8\" disabled=\"\">", false, false},
        {"<input id=c type=\"number\" value=\"8\" step=\"any\" min=\"2\" max=\"8\" readonly=\"\">", false, false},
        {"<input id=c type=\"number\" value=\"9\" step=\"any\" min=\"2\" max=\"8\">", false, true},
        {"<input id=c type=\"number\" value=\"9\" step=\"any\">", false, false},
        {"<input id=c type=\"number\" value=\"9\" step=\"any\" min=\"bad\" max=\"bad\">", false, false},
        {"<input id=c type=\"number\" value=\"9\" step=\"any\" min=\"2\" max=\"8\" disabled=\"\">", false, false},
        {"<input id=c type=\"number\" value=\"9\" step=\"any\" min=\"2\" max=\"8\" readonly=\"\">", false, false},
        {"<input id=c type=\"number\" value=\"bad\" step=\"any\" min=\"2\" max=\"8\">", true, false},
        {"<input id=c type=\"number\" value=\"bad\" step=\"any\">", true, false},
        {"<input id=c type=\"number\" value=\"bad\" step=\"any\" min=\"bad\" max=\"bad\">", true, false},
        {"<input id=c type=\"number\" value=\"bad\" step=\"any\" min=\"2\" max=\"8\" disabled=\"\">", false, false},
        {"<input id=c type=\"number\" value=\"bad\" step=\"any\" min=\"2\" max=\"8\" readonly=\"\">", false, false},
        {"<input id=c type=\"range\" value=\"\" step=\"any\" min=\"2\" max=\"8\">", true, false},
        {"<input id=c type=\"range\" value=\"\" step=\"any\">", true, false},
        {"<input id=c type=\"range\" value=\"\" step=\"any\" min=\"bad\" max=\"bad\">", true, false},
        {"<input id=c type=\"range\" value=\"\" step=\"any\" min=\"2\" max=\"8\" disabled=\"\">", false, false},
        {"<input id=c type=\"range\" value=\"\" step=\"any\" min=\"2\" max=\"8\" readonly=\"\">", false, false},
        {"<input id=c type=\"range\" value=\"1\" step=\"any\" min=\"2\" max=\"8\">", true, false},
        {"<input id=c type=\"range\" value=\"1\" step=\"any\">", true, false},
        {"<input id=c type=\"range\" value=\"1\" step=\"any\" min=\"bad\" max=\"bad\">", true, false},
        {"<input id=c type=\"range\" value=\"1\" step=\"any\" min=\"2\" max=\"8\" disabled=\"\">", false, false},
        {"<input id=c type=\"range\" value=\"1\" step=\"any\" min=\"2\" max=\"8\" readonly=\"\">", false, false},
        {"<input id=c type=\"range\" value=\"5\" step=\"any\" min=\"2\" max=\"8\">", true, false},
        {"<input id=c type=\"range\" value=\"5\" step=\"any\">", true, false},
        {"<input id=c type=\"range\" value=\"5\" step=\"any\" min=\"bad\" max=\"bad\">", true, false},
        {"<input id=c type=\"range\" value=\"5\" step=\"any\" min=\"2\" max=\"8\" disabled=\"\">", false, false},
        {"<input id=c type=\"range\" value=\"5\" step=\"any\" min=\"2\" max=\"8\" readonly=\"\">", false, false},
        {"<input id=c type=\"range\" value=\"9\" step=\"any\" min=\"2\" max=\"8\">", true, false},
        {"<input id=c type=\"range\" value=\"9\" step=\"any\">", true, false},
        {"<input id=c type=\"range\" value=\"9\" step=\"any\" min=\"bad\" max=\"bad\">", true, false},
        {"<input id=c type=\"range\" value=\"9\" step=\"any\" min=\"2\" max=\"8\" disabled=\"\">", false, false},
        {"<input id=c type=\"range\" value=\"9\" step=\"any\" min=\"2\" max=\"8\" readonly=\"\">", false, false},
        {"<input id=c type=\"date\" value=\"\" step=\"any\" min=\"2026-01-02\" max=\"2026-01-08\">", true, false},
        {"<input id=c type=\"date\" value=\"\" step=\"any\">", true, false},
        {"<input id=c type=\"date\" value=\"\" step=\"any\" min=\"bad\" max=\"bad\">", true, false},
        {"<input id=c type=\"date\" value=\"\" step=\"any\" min=\"2026-01-02\" max=\"2026-01-08\" disabled=\"\">", false, false},
        {"<input id=c type=\"date\" value=\"\" step=\"any\" min=\"2026-01-02\" max=\"2026-01-08\" readonly=\"\">", false, false},
        {"<input id=c type=\"date\" value=\"2026-01-01\" step=\"any\" min=\"2026-01-02\" max=\"2026-01-08\">", false, true},
        {"<input id=c type=\"date\" value=\"2026-01-01\" step=\"any\">", false, false},
        {"<input id=c type=\"date\" value=\"2026-01-01\" step=\"any\" min=\"bad\" max=\"bad\">", false, false},
        {"<input id=c type=\"date\" value=\"2026-01-01\" step=\"any\" min=\"2026-01-02\" max=\"2026-01-08\" disabled=\"\">", false, false},
        {"<input id=c type=\"date\" value=\"2026-01-01\" step=\"any\" min=\"2026-01-02\" max=\"2026-01-08\" readonly=\"\">", false, false},
        {"<input id=c type=\"date\" value=\"2026-01-05\" step=\"any\" min=\"2026-01-02\" max=\"2026-01-08\">", true, false},
        {"<input id=c type=\"date\" value=\"2026-01-05\" step=\"any\">", false, false},
        {"<input id=c type=\"date\" value=\"2026-01-05\" step=\"any\" min=\"bad\" max=\"bad\">", false, false},
        {"<input id=c type=\"date\" value=\"2026-01-05\" step=\"any\" min=\"2026-01-02\" max=\"2026-01-08\" disabled=\"\">", false, false},
        {"<input id=c type=\"date\" value=\"2026-01-05\" step=\"any\" min=\"2026-01-02\" max=\"2026-01-08\" readonly=\"\">", false, false},
        {"<input id=c type=\"date\" value=\"2026-01-09\" step=\"any\" min=\"2026-01-02\" max=\"2026-01-08\">", false, true},
        {"<input id=c type=\"date\" value=\"2026-01-09\" step=\"any\">", false, false},
        {"<input id=c type=\"date\" value=\"2026-01-09\" step=\"any\" min=\"bad\" max=\"bad\">", false, false},
        {"<input id=c type=\"date\" value=\"2026-01-09\" step=\"any\" min=\"2026-01-02\" max=\"2026-01-08\" disabled=\"\">", false, false},
        {"<input id=c type=\"date\" value=\"2026-01-09\" step=\"any\" min=\"2026-01-02\" max=\"2026-01-08\" readonly=\"\">", false, false},
        {"<input id=c type=\"time\" value=\"\" step=\"any\" min=\"08:00\" max=\"17:00\">", true, false},
        {"<input id=c type=\"time\" value=\"\" step=\"any\">", true, false},
        {"<input id=c type=\"time\" value=\"\" step=\"any\" min=\"bad\" max=\"bad\">", true, false},
        {"<input id=c type=\"time\" value=\"\" step=\"any\" min=\"08:00\" max=\"17:00\" disabled=\"\">", false, false},
        {"<input id=c type=\"time\" value=\"\" step=\"any\" min=\"08:00\" max=\"17:00\" readonly=\"\">", false, false},
        {"<input id=c type=\"time\" value=\"07:00\" step=\"any\" min=\"08:00\" max=\"17:00\">", false, true},
        {"<input id=c type=\"time\" value=\"07:00\" step=\"any\">", false, false},
        {"<input id=c type=\"time\" value=\"07:00\" step=\"any\" min=\"bad\" max=\"bad\">", false, false},
        {"<input id=c type=\"time\" value=\"07:00\" step=\"any\" min=\"08:00\" max=\"17:00\" disabled=\"\">", false, false},
        {"<input id=c type=\"time\" value=\"07:00\" step=\"any\" min=\"08:00\" max=\"17:00\" readonly=\"\">", false, false},
        {"<input id=c type=\"time\" value=\"12:00\" step=\"any\" min=\"08:00\" max=\"17:00\">", true, false},
        {"<input id=c type=\"time\" value=\"12:00\" step=\"any\">", false, false},
        {"<input id=c type=\"time\" value=\"12:00\" step=\"any\" min=\"bad\" max=\"bad\">", false, false},
        {"<input id=c type=\"time\" value=\"12:00\" step=\"any\" min=\"08:00\" max=\"17:00\" disabled=\"\">", false, false},
        {"<input id=c type=\"time\" value=\"12:00\" step=\"any\" min=\"08:00\" max=\"17:00\" readonly=\"\">", false, false},
        {"<input id=c type=\"time\" value=\"18:00\" step=\"any\" min=\"08:00\" max=\"17:00\">", false, true},
        {"<input id=c type=\"time\" value=\"18:00\" step=\"any\">", false, false},
        {"<input id=c type=\"time\" value=\"18:00\" step=\"any\" min=\"bad\" max=\"bad\">", false, false},
        {"<input id=c type=\"time\" value=\"18:00\" step=\"any\" min=\"08:00\" max=\"17:00\" disabled=\"\">", false, false},
        {"<input id=c type=\"time\" value=\"18:00\" step=\"any\" min=\"08:00\" max=\"17:00\" readonly=\"\">", false, false},
        {"<input id=c type=\"time\" value=\"\" step=\"any\" min=\"22:00\" max=\"06:00\">", true, false},
        {"<input id=c type=\"time\" value=\"\" step=\"any\">", true, false},
        {"<input id=c type=\"time\" value=\"\" step=\"any\" min=\"bad\" max=\"bad\">", true, false},
        {"<input id=c type=\"time\" value=\"\" step=\"any\" min=\"22:00\" max=\"06:00\" disabled=\"\">", false, false},
        {"<input id=c type=\"time\" value=\"\" step=\"any\" min=\"22:00\" max=\"06:00\" readonly=\"\">", false, false},
        {"<input id=c type=\"time\" value=\"01:00\" step=\"any\" min=\"22:00\" max=\"06:00\">", true, false},
        {"<input id=c type=\"time\" value=\"01:00\" step=\"any\">", false, false},
        {"<input id=c type=\"time\" value=\"01:00\" step=\"any\" min=\"bad\" max=\"bad\">", false, false},
        {"<input id=c type=\"time\" value=\"01:00\" step=\"any\" min=\"22:00\" max=\"06:00\" disabled=\"\">", false, false},
        {"<input id=c type=\"time\" value=\"01:00\" step=\"any\" min=\"22:00\" max=\"06:00\" readonly=\"\">", false, false},
        {"<input id=c type=\"time\" value=\"12:00\" step=\"any\" min=\"22:00\" max=\"06:00\">", false, true},
        {"<input id=c type=\"time\" value=\"12:00\" step=\"any\">", false, false},
        {"<input id=c type=\"time\" value=\"12:00\" step=\"any\" min=\"bad\" max=\"bad\">", false, false},
        {"<input id=c type=\"time\" value=\"12:00\" step=\"any\" min=\"22:00\" max=\"06:00\" disabled=\"\">", false, false},
        {"<input id=c type=\"time\" value=\"12:00\" step=\"any\" min=\"22:00\" max=\"06:00\" readonly=\"\">", false, false},
        {"<input id=c type=\"time\" value=\"23:00\" step=\"any\" min=\"22:00\" max=\"06:00\">", true, false},
        {"<input id=c type=\"time\" value=\"23:00\" step=\"any\">", false, false},
        {"<input id=c type=\"time\" value=\"23:00\" step=\"any\" min=\"bad\" max=\"bad\">", false, false},
        {"<input id=c type=\"time\" value=\"23:00\" step=\"any\" min=\"22:00\" max=\"06:00\" disabled=\"\">", false, false},
        {"<input id=c type=\"time\" value=\"23:00\" step=\"any\" min=\"22:00\" max=\"06:00\" readonly=\"\">", false, false},
        {"<input id=c type=\"month\" value=\"\" step=\"any\" min=\"2026-02\" max=\"2026-08\">", true, false},
        {"<input id=c type=\"month\" value=\"\" step=\"any\">", true, false},
        {"<input id=c type=\"month\" value=\"\" step=\"any\" min=\"bad\" max=\"bad\">", true, false},
        {"<input id=c type=\"month\" value=\"\" step=\"any\" min=\"2026-02\" max=\"2026-08\" disabled=\"\">", false, false},
        {"<input id=c type=\"month\" value=\"\" step=\"any\" min=\"2026-02\" max=\"2026-08\" readonly=\"\">", false, false},
        {"<input id=c type=\"month\" value=\"2026-01\" step=\"any\" min=\"2026-02\" max=\"2026-08\">", false, true},
        {"<input id=c type=\"month\" value=\"2026-01\" step=\"any\">", false, false},
        {"<input id=c type=\"month\" value=\"2026-01\" step=\"any\" min=\"bad\" max=\"bad\">", false, false},
        {"<input id=c type=\"month\" value=\"2026-01\" step=\"any\" min=\"2026-02\" max=\"2026-08\" disabled=\"\">", false, false},
        {"<input id=c type=\"month\" value=\"2026-01\" step=\"any\" min=\"2026-02\" max=\"2026-08\" readonly=\"\">", false, false},
        {"<input id=c type=\"month\" value=\"2026-05\" step=\"any\" min=\"2026-02\" max=\"2026-08\">", true, false},
        {"<input id=c type=\"month\" value=\"2026-05\" step=\"any\">", false, false},
        {"<input id=c type=\"month\" value=\"2026-05\" step=\"any\" min=\"bad\" max=\"bad\">", false, false},
        {"<input id=c type=\"month\" value=\"2026-05\" step=\"any\" min=\"2026-02\" max=\"2026-08\" disabled=\"\">", false, false},
        {"<input id=c type=\"month\" value=\"2026-05\" step=\"any\" min=\"2026-02\" max=\"2026-08\" readonly=\"\">", false, false},
        {"<input id=c type=\"month\" value=\"2026-09\" step=\"any\" min=\"2026-02\" max=\"2026-08\">", false, true},
        {"<input id=c type=\"month\" value=\"2026-09\" step=\"any\">", false, false},
        {"<input id=c type=\"month\" value=\"2026-09\" step=\"any\" min=\"bad\" max=\"bad\">", false, false},
        {"<input id=c type=\"month\" value=\"2026-09\" step=\"any\" min=\"2026-02\" max=\"2026-08\" disabled=\"\">", false, false},
        {"<input id=c type=\"month\" value=\"2026-09\" step=\"any\" min=\"2026-02\" max=\"2026-08\" readonly=\"\">", false, false},
        {"<input id=c type=\"week\" value=\"\" step=\"any\" min=\"2026-W02\" max=\"2026-W08\">", true, false},
        {"<input id=c type=\"week\" value=\"\" step=\"any\">", true, false},
        {"<input id=c type=\"week\" value=\"\" step=\"any\" min=\"bad\" max=\"bad\">", true, false},
        {"<input id=c type=\"week\" value=\"\" step=\"any\" min=\"2026-W02\" max=\"2026-W08\" disabled=\"\">", false, false},
        {"<input id=c type=\"week\" value=\"\" step=\"any\" min=\"2026-W02\" max=\"2026-W08\" readonly=\"\">", false, false},
        {"<input id=c type=\"week\" value=\"2026-W01\" step=\"any\" min=\"2026-W02\" max=\"2026-W08\">", false, true},
        {"<input id=c type=\"week\" value=\"2026-W01\" step=\"any\">", false, false},
        {"<input id=c type=\"week\" value=\"2026-W01\" step=\"any\" min=\"bad\" max=\"bad\">", false, false},
        {"<input id=c type=\"week\" value=\"2026-W01\" step=\"any\" min=\"2026-W02\" max=\"2026-W08\" disabled=\"\">", false, false},
        {"<input id=c type=\"week\" value=\"2026-W01\" step=\"any\" min=\"2026-W02\" max=\"2026-W08\" readonly=\"\">", false, false},
        {"<input id=c type=\"week\" value=\"2026-W05\" step=\"any\" min=\"2026-W02\" max=\"2026-W08\">", true, false},
        {"<input id=c type=\"week\" value=\"2026-W05\" step=\"any\">", false, false},
        {"<input id=c type=\"week\" value=\"2026-W05\" step=\"any\" min=\"bad\" max=\"bad\">", false, false},
        {"<input id=c type=\"week\" value=\"2026-W05\" step=\"any\" min=\"2026-W02\" max=\"2026-W08\" disabled=\"\">", false, false},
        {"<input id=c type=\"week\" value=\"2026-W05\" step=\"any\" min=\"2026-W02\" max=\"2026-W08\" readonly=\"\">", false, false},
        {"<input id=c type=\"week\" value=\"2026-W09\" step=\"any\" min=\"2026-W02\" max=\"2026-W08\">", false, true},
        {"<input id=c type=\"week\" value=\"2026-W09\" step=\"any\">", false, false},
        {"<input id=c type=\"week\" value=\"2026-W09\" step=\"any\" min=\"bad\" max=\"bad\">", false, false},
        {"<input id=c type=\"week\" value=\"2026-W09\" step=\"any\" min=\"2026-W02\" max=\"2026-W08\" disabled=\"\">", false, false},
        {"<input id=c type=\"week\" value=\"2026-W09\" step=\"any\" min=\"2026-W02\" max=\"2026-W08\" readonly=\"\">", false, false},
        {"<input id=c type=\"datetime-local\" value=\"\" step=\"any\" min=\"2026-01-02T00:00\" max=\"2026-01-08T00:00\">", true, false},
        {"<input id=c type=\"datetime-local\" value=\"\" step=\"any\">", true, false},
        {"<input id=c type=\"datetime-local\" value=\"\" step=\"any\" min=\"bad\" max=\"bad\">", true, false},
        {"<input id=c type=\"datetime-local\" value=\"\" step=\"any\" min=\"2026-01-02T00:00\" max=\"2026-01-08T00:00\" disabled=\"\">", false, false},
        {"<input id=c type=\"datetime-local\" value=\"\" step=\"any\" min=\"2026-01-02T00:00\" max=\"2026-01-08T00:00\" readonly=\"\">", false, false},
        {"<input id=c type=\"datetime-local\" value=\"2026-01-01T00:00\" step=\"any\" min=\"2026-01-02T00:00\" max=\"2026-01-08T00:00\">", false, true},
        {"<input id=c type=\"datetime-local\" value=\"2026-01-01T00:00\" step=\"any\">", false, false},
        {"<input id=c type=\"datetime-local\" value=\"2026-01-01T00:00\" step=\"any\" min=\"bad\" max=\"bad\">", false, false},
        {"<input id=c type=\"datetime-local\" value=\"2026-01-01T00:00\" step=\"any\" min=\"2026-01-02T00:00\" max=\"2026-01-08T00:00\" disabled=\"\">", false, false},
        {"<input id=c type=\"datetime-local\" value=\"2026-01-01T00:00\" step=\"any\" min=\"2026-01-02T00:00\" max=\"2026-01-08T00:00\" readonly=\"\">", false, false},
        {"<input id=c type=\"datetime-local\" value=\"2026-01-05T00:00\" step=\"any\" min=\"2026-01-02T00:00\" max=\"2026-01-08T00:00\">", true, false},
        {"<input id=c type=\"datetime-local\" value=\"2026-01-05T00:00\" step=\"any\">", false, false},
        {"<input id=c type=\"datetime-local\" value=\"2026-01-05T00:00\" step=\"any\" min=\"bad\" max=\"bad\">", false, false},
        {"<input id=c type=\"datetime-local\" value=\"2026-01-05T00:00\" step=\"any\" min=\"2026-01-02T00:00\" max=\"2026-01-08T00:00\" disabled=\"\">", false, false},
        {"<input id=c type=\"datetime-local\" value=\"2026-01-05T00:00\" step=\"any\" min=\"2026-01-02T00:00\" max=\"2026-01-08T00:00\" readonly=\"\">", false, false},
        {"<input id=c type=\"datetime-local\" value=\"2026-01-09T00:00\" step=\"any\" min=\"2026-01-02T00:00\" max=\"2026-01-08T00:00\">", false, true},
        {"<input id=c type=\"datetime-local\" value=\"2026-01-09T00:00\" step=\"any\">", false, false},
        {"<input id=c type=\"datetime-local\" value=\"2026-01-09T00:00\" step=\"any\" min=\"bad\" max=\"bad\">", false, false},
        {"<input id=c type=\"datetime-local\" value=\"2026-01-09T00:00\" step=\"any\" min=\"2026-01-02T00:00\" max=\"2026-01-08T00:00\" disabled=\"\">", false, false},
        {"<input id=c type=\"datetime-local\" value=\"2026-01-09T00:00\" step=\"any\" min=\"2026-01-02T00:00\" max=\"2026-01-08T00:00\" readonly=\"\">", false, false},
        {"<input id=c type=\"text\" value=\"\" step=\"any\" min=\"2\" max=\"8\">", false, false},
        {"<input id=c type=\"text\" value=\"\" step=\"any\">", false, false},
        {"<input id=c type=\"text\" value=\"\" step=\"any\" min=\"bad\" max=\"bad\">", false, false},
        {"<input id=c type=\"text\" value=\"\" step=\"any\" min=\"2\" max=\"8\" disabled=\"\">", false, false},
        {"<input id=c type=\"text\" value=\"\" step=\"any\" min=\"2\" max=\"8\" readonly=\"\">", false, false},
        {"<input id=c type=\"text\" value=\"1\" step=\"any\" min=\"2\" max=\"8\">", false, false},
        {"<input id=c type=\"text\" value=\"1\" step=\"any\">", false, false},
        {"<input id=c type=\"text\" value=\"1\" step=\"any\" min=\"bad\" max=\"bad\">", false, false},
        {"<input id=c type=\"text\" value=\"1\" step=\"any\" min=\"2\" max=\"8\" disabled=\"\">", false, false},
        {"<input id=c type=\"text\" value=\"1\" step=\"any\" min=\"2\" max=\"8\" readonly=\"\">", false, false},
        {"<input id=c type=\"text\" value=\"5\" step=\"any\" min=\"2\" max=\"8\">", false, false},
        {"<input id=c type=\"text\" value=\"5\" step=\"any\">", false, false},
        {"<input id=c type=\"text\" value=\"5\" step=\"any\" min=\"bad\" max=\"bad\">", false, false},
        {"<input id=c type=\"text\" value=\"5\" step=\"any\" min=\"2\" max=\"8\" disabled=\"\">", false, false},
        {"<input id=c type=\"text\" value=\"5\" step=\"any\" min=\"2\" max=\"8\" readonly=\"\">", false, false},
        {"<input id=c type=\"text\" value=\"9\" step=\"any\" min=\"2\" max=\"8\">", false, false},
        {"<input id=c type=\"text\" value=\"9\" step=\"any\">", false, false},
        {"<input id=c type=\"text\" value=\"9\" step=\"any\" min=\"bad\" max=\"bad\">", false, false},
        {"<input id=c type=\"text\" value=\"9\" step=\"any\" min=\"2\" max=\"8\" disabled=\"\">", false, false},
        {"<input id=c type=\"text\" value=\"9\" step=\"any\" min=\"2\" max=\"8\" readonly=\"\">", false, false},
    };
    for (const auto& row : rows) {
        Form f(row.html);
        CHECK((f.at("#c:in-range") != WEVA_ELEMENT_NONE) == row.inside);
        CHECK((f.at("#c:out-of-range") != WEVA_ELEMENT_NONE) == row.outside);
    }
    Form f("<section id=p><input id=c type=number min=2 max=8 value=5></section>");
    const char* css = "input{width:100px;min-width:0;box-sizing:border-box}input:in-range{width:120px}input:out-of-range{width:160px}section:has(:out-of-range){width:210px}section{width:200px}";
    CHECK(weva_document_set_css(f.doc, css, std::strlen(css)) == WEVA_OK);

    auto width = [&](const char* selector) {
        CHECK(weva_document_update(f.doc, 0) == WEVA_OK);
        double x, y, w, h;
        CHECK(weva_element_bounds(f.doc, f.at(selector), &x, &y, &w, &h) == WEVA_OK);
        return w;
    };
    CHECK(width("#c") == 120);
    f.set("#c", "6"); CHECK(width("#c") == 120); CHECK(width("#p") == 200);
    f.set("#c", "9"); CHECK(width("#c") == 160); CHECK(width("#p") == 210);
    f.set("#c", "10"); CHECK(width("#c") == 160); CHECK(width("#p") == 210);
    f.set("#c", "5"); CHECK(width("#c") == 120); CHECK(width("#p") == 200);
    f.attr("#c", "disabled", ""); CHECK(width("#c") == 100);
    f.attr("#c", "disabled", nullptr); CHECK(width("#c") == 120);
    f.attr("#c", "max", "4"); CHECK(width("#c") == 160);
    f.attr("#c", "max", "8"); CHECK(width("#c") == 120);
    // Exercise nested range selectors in the shared selector cache too.
    const char* cached_css = "input{width:100px;min-width:0;box-sizing:border-box}input:is(:in-range){width:120px}input:not(:in-range):out-of-range{width:160px}";
    CHECK(weva_document_set_css(f.doc, cached_css, std::strlen(cached_css)) == WEVA_OK);
    CHECK(width("#c") == 120);
    f.set("#c", "9"); CHECK(width("#c") == 160);
    f.set("#c", "5"); CHECK(width("#c") == 120);
}

void test_validity_selectors_chrome() {
    {
        auto parent = make_ref<Element>("fieldset");
        auto field = make_ref<Element>("input");
        parent->set_attribute("disabled", "");
        field->set_attribute("required", "");
        parent->append_child(field.get());
        CHECK(form_validity_selector_state(*field) == 0);
        CHECK(form_validity_selector_state(*parent) == 1);
        parent->remove_child(field.get());
        CHECK(form_validity_selector_state(*field) == 2);
        CHECK(form_validity_selector_state(*parent) == 1);
        parent->append_child(field.get());
        CHECK(form_validity_selector_state(*field) == 0);
        bool observed = false;
        parent->add_observer([&](const DomMutation&) {
            observed = true;
            CHECK(form_validity_selector_state(*field) == 2);
            CHECK(form_validity_selector_state(*parent) == 2);
        });
        parent->remove_attribute("disabled");
        CHECK(observed);
    }
    { // text/plain
        Form f("<form id=f><fieldset id=s><input id=c type=\"text\"   ></fieldset></form>");
        CHECK((f.at("#f:valid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#f:invalid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#s:valid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#s:invalid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#c:valid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#c:invalid") != WEVA_ELEMENT_NONE) == false);
    }
    { // text/required
        Form f("<form id=f><fieldset id=s><input id=c type=\"text\" required  ></fieldset></form>");
        CHECK((f.at("#f:valid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#f:invalid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#s:valid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#s:invalid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#c:valid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#c:invalid") != WEVA_ELEMENT_NONE) == true);
    }
    { // text/disabled
        Form f("<form id=f><fieldset id=s><input id=c type=\"text\"  required disabled ></fieldset></form>");
        CHECK((f.at("#f:valid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#f:invalid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#s:valid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#s:invalid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#c:valid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#c:invalid") != WEVA_ELEMENT_NONE) == false);
    }
    { // text/readonly
        Form f("<form id=f><fieldset id=s><input id=c type=\"text\"   required readonly></fieldset></form>");
        CHECK((f.at("#f:valid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#f:invalid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#s:valid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#s:invalid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#c:valid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#c:invalid") != WEVA_ELEMENT_NONE) == false);
    }
    { // text/custom
        Form f("<form id=f><fieldset id=s><input id=c type=\"text\"   ></fieldset></form>");
        CHECK(weva_element_set_custom_validity(f.doc, f.at("#c"), "Reserved") == WEVA_OK);
        CHECK((f.at("#f:valid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#f:invalid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#s:valid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#s:invalid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#c:valid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#c:invalid") != WEVA_ELEMENT_NONE) == true);
    }
    { // number/plain
        Form f("<form id=f><fieldset id=s><input id=c type=\"number\"   ></fieldset></form>");
        CHECK((f.at("#f:valid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#f:invalid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#s:valid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#s:invalid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#c:valid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#c:invalid") != WEVA_ELEMENT_NONE) == false);
    }
    { // number/required
        Form f("<form id=f><fieldset id=s><input id=c type=\"number\" required  ></fieldset></form>");
        CHECK((f.at("#f:valid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#f:invalid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#s:valid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#s:invalid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#c:valid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#c:invalid") != WEVA_ELEMENT_NONE) == true);
    }
    { // number/disabled
        Form f("<form id=f><fieldset id=s><input id=c type=\"number\"  required disabled ></fieldset></form>");
        CHECK((f.at("#f:valid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#f:invalid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#s:valid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#s:invalid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#c:valid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#c:invalid") != WEVA_ELEMENT_NONE) == false);
    }
    { // number/readonly
        Form f("<form id=f><fieldset id=s><input id=c type=\"number\"   required readonly></fieldset></form>");
        CHECK((f.at("#f:valid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#f:invalid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#s:valid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#s:invalid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#c:valid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#c:invalid") != WEVA_ELEMENT_NONE) == false);
    }
    { // number/custom
        Form f("<form id=f><fieldset id=s><input id=c type=\"number\"   ></fieldset></form>");
        CHECK(weva_element_set_custom_validity(f.doc, f.at("#c"), "Reserved") == WEVA_OK);
        CHECK((f.at("#f:valid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#f:invalid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#s:valid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#s:invalid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#c:valid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#c:invalid") != WEVA_ELEMENT_NONE) == true);
    }
    { // email/plain
        Form f("<form id=f><fieldset id=s><input id=c type=\"email\"   ></fieldset></form>");
        CHECK((f.at("#f:valid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#f:invalid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#s:valid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#s:invalid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#c:valid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#c:invalid") != WEVA_ELEMENT_NONE) == false);
    }
    { // email/required
        Form f("<form id=f><fieldset id=s><input id=c type=\"email\" required  ></fieldset></form>");
        CHECK((f.at("#f:valid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#f:invalid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#s:valid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#s:invalid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#c:valid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#c:invalid") != WEVA_ELEMENT_NONE) == true);
    }
    { // email/disabled
        Form f("<form id=f><fieldset id=s><input id=c type=\"email\"  required disabled ></fieldset></form>");
        CHECK((f.at("#f:valid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#f:invalid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#s:valid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#s:invalid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#c:valid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#c:invalid") != WEVA_ELEMENT_NONE) == false);
    }
    { // email/readonly
        Form f("<form id=f><fieldset id=s><input id=c type=\"email\"   required readonly></fieldset></form>");
        CHECK((f.at("#f:valid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#f:invalid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#s:valid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#s:invalid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#c:valid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#c:invalid") != WEVA_ELEMENT_NONE) == false);
    }
    { // email/custom
        Form f("<form id=f><fieldset id=s><input id=c type=\"email\"   ></fieldset></form>");
        CHECK(weva_element_set_custom_validity(f.doc, f.at("#c"), "Reserved") == WEVA_OK);
        CHECK((f.at("#f:valid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#f:invalid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#s:valid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#s:invalid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#c:valid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#c:invalid") != WEVA_ELEMENT_NONE) == true);
    }
    { // url/plain
        Form f("<form id=f><fieldset id=s><input id=c type=\"url\"   ></fieldset></form>");
        CHECK((f.at("#f:valid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#f:invalid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#s:valid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#s:invalid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#c:valid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#c:invalid") != WEVA_ELEMENT_NONE) == false);
    }
    { // url/required
        Form f("<form id=f><fieldset id=s><input id=c type=\"url\" required  ></fieldset></form>");
        CHECK((f.at("#f:valid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#f:invalid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#s:valid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#s:invalid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#c:valid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#c:invalid") != WEVA_ELEMENT_NONE) == true);
    }
    { // url/disabled
        Form f("<form id=f><fieldset id=s><input id=c type=\"url\"  required disabled ></fieldset></form>");
        CHECK((f.at("#f:valid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#f:invalid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#s:valid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#s:invalid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#c:valid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#c:invalid") != WEVA_ELEMENT_NONE) == false);
    }
    { // url/readonly
        Form f("<form id=f><fieldset id=s><input id=c type=\"url\"   required readonly></fieldset></form>");
        CHECK((f.at("#f:valid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#f:invalid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#s:valid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#s:invalid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#c:valid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#c:invalid") != WEVA_ELEMENT_NONE) == false);
    }
    { // url/custom
        Form f("<form id=f><fieldset id=s><input id=c type=\"url\"   ></fieldset></form>");
        CHECK(weva_element_set_custom_validity(f.doc, f.at("#c"), "Reserved") == WEVA_OK);
        CHECK((f.at("#f:valid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#f:invalid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#s:valid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#s:invalid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#c:valid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#c:invalid") != WEVA_ELEMENT_NONE) == true);
    }
    { // checkbox/plain
        Form f("<form id=f><fieldset id=s><input id=c type=\"checkbox\"   ></fieldset></form>");
        CHECK((f.at("#f:valid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#f:invalid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#s:valid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#s:invalid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#c:valid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#c:invalid") != WEVA_ELEMENT_NONE) == false);
    }
    { // checkbox/required
        Form f("<form id=f><fieldset id=s><input id=c type=\"checkbox\" required  ></fieldset></form>");
        CHECK((f.at("#f:valid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#f:invalid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#s:valid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#s:invalid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#c:valid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#c:invalid") != WEVA_ELEMENT_NONE) == true);
    }
    { // checkbox/disabled
        Form f("<form id=f><fieldset id=s><input id=c type=\"checkbox\"  required disabled ></fieldset></form>");
        CHECK((f.at("#f:valid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#f:invalid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#s:valid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#s:invalid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#c:valid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#c:invalid") != WEVA_ELEMENT_NONE) == false);
    }
    { // checkbox/readonly
        Form f("<form id=f><fieldset id=s><input id=c type=\"checkbox\"   required readonly></fieldset></form>");
        CHECK((f.at("#f:valid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#f:invalid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#s:valid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#s:invalid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#c:valid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#c:invalid") != WEVA_ELEMENT_NONE) == false);
    }
    { // checkbox/custom
        Form f("<form id=f><fieldset id=s><input id=c type=\"checkbox\"   ></fieldset></form>");
        CHECK(weva_element_set_custom_validity(f.doc, f.at("#c"), "Reserved") == WEVA_OK);
        CHECK((f.at("#f:valid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#f:invalid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#s:valid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#s:invalid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#c:valid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#c:invalid") != WEVA_ELEMENT_NONE) == true);
    }
    { // radio/plain
        Form f("<form id=f><fieldset id=s><input id=c type=\"radio\"   ></fieldset></form>");
        CHECK((f.at("#f:valid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#f:invalid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#s:valid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#s:invalid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#c:valid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#c:invalid") != WEVA_ELEMENT_NONE) == false);
    }
    { // radio/required
        Form f("<form id=f><fieldset id=s><input id=c type=\"radio\" required  ></fieldset></form>");
        CHECK((f.at("#f:valid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#f:invalid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#s:valid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#s:invalid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#c:valid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#c:invalid") != WEVA_ELEMENT_NONE) == false);
    }
    { // radio/disabled
        Form f("<form id=f><fieldset id=s><input id=c type=\"radio\"  required disabled ></fieldset></form>");
        CHECK((f.at("#f:valid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#f:invalid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#s:valid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#s:invalid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#c:valid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#c:invalid") != WEVA_ELEMENT_NONE) == false);
    }
    { // radio/readonly
        Form f("<form id=f><fieldset id=s><input id=c type=\"radio\"   required readonly></fieldset></form>");
        CHECK((f.at("#f:valid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#f:invalid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#s:valid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#s:invalid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#c:valid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#c:invalid") != WEVA_ELEMENT_NONE) == false);
    }
    { // radio/custom
        Form f("<form id=f><fieldset id=s><input id=c type=\"radio\"   ></fieldset></form>");
        CHECK(weva_element_set_custom_validity(f.doc, f.at("#c"), "Reserved") == WEVA_OK);
        CHECK((f.at("#f:valid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#f:invalid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#s:valid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#s:invalid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#c:valid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#c:invalid") != WEVA_ELEMENT_NONE) == true);
    }
    { // range/plain
        Form f("<form id=f><fieldset id=s><input id=c type=\"range\"   ></fieldset></form>");
        CHECK((f.at("#f:valid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#f:invalid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#s:valid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#s:invalid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#c:valid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#c:invalid") != WEVA_ELEMENT_NONE) == false);
    }
    { // range/required
        Form f("<form id=f><fieldset id=s><input id=c type=\"range\" required  ></fieldset></form>");
        CHECK((f.at("#f:valid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#f:invalid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#s:valid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#s:invalid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#c:valid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#c:invalid") != WEVA_ELEMENT_NONE) == false);
    }
    { // range/disabled
        Form f("<form id=f><fieldset id=s><input id=c type=\"range\"  required disabled ></fieldset></form>");
        CHECK((f.at("#f:valid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#f:invalid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#s:valid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#s:invalid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#c:valid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#c:invalid") != WEVA_ELEMENT_NONE) == false);
    }
    { // range/readonly
        Form f("<form id=f><fieldset id=s><input id=c type=\"range\"   required readonly></fieldset></form>");
        CHECK((f.at("#f:valid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#f:invalid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#s:valid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#s:invalid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#c:valid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#c:invalid") != WEVA_ELEMENT_NONE) == false);
    }
    { // range/custom
        Form f("<form id=f><fieldset id=s><input id=c type=\"range\"   ></fieldset></form>");
        CHECK(weva_element_set_custom_validity(f.doc, f.at("#c"), "Reserved") == WEVA_OK);
        CHECK((f.at("#f:valid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#f:invalid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#s:valid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#s:invalid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#c:valid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#c:invalid") != WEVA_ELEMENT_NONE) == true);
    }
    { // date/plain
        Form f("<form id=f><fieldset id=s><input id=c type=\"date\"   ></fieldset></form>");
        CHECK((f.at("#f:valid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#f:invalid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#s:valid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#s:invalid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#c:valid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#c:invalid") != WEVA_ELEMENT_NONE) == false);
    }
    { // date/required
        Form f("<form id=f><fieldset id=s><input id=c type=\"date\" required  ></fieldset></form>");
        CHECK((f.at("#f:valid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#f:invalid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#s:valid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#s:invalid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#c:valid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#c:invalid") != WEVA_ELEMENT_NONE) == true);
    }
    { // date/disabled
        Form f("<form id=f><fieldset id=s><input id=c type=\"date\"  required disabled ></fieldset></form>");
        CHECK((f.at("#f:valid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#f:invalid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#s:valid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#s:invalid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#c:valid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#c:invalid") != WEVA_ELEMENT_NONE) == false);
    }
    { // date/readonly
        Form f("<form id=f><fieldset id=s><input id=c type=\"date\"   required readonly></fieldset></form>");
        CHECK((f.at("#f:valid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#f:invalid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#s:valid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#s:invalid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#c:valid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#c:invalid") != WEVA_ELEMENT_NONE) == false);
    }
    { // date/custom
        Form f("<form id=f><fieldset id=s><input id=c type=\"date\"   ></fieldset></form>");
        CHECK(weva_element_set_custom_validity(f.doc, f.at("#c"), "Reserved") == WEVA_OK);
        CHECK((f.at("#f:valid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#f:invalid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#s:valid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#s:invalid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#c:valid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#c:invalid") != WEVA_ELEMENT_NONE) == true);
    }
    { // time/plain
        Form f("<form id=f><fieldset id=s><input id=c type=\"time\"   ></fieldset></form>");
        CHECK((f.at("#f:valid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#f:invalid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#s:valid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#s:invalid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#c:valid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#c:invalid") != WEVA_ELEMENT_NONE) == false);
    }
    { // time/required
        Form f("<form id=f><fieldset id=s><input id=c type=\"time\" required  ></fieldset></form>");
        CHECK((f.at("#f:valid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#f:invalid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#s:valid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#s:invalid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#c:valid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#c:invalid") != WEVA_ELEMENT_NONE) == true);
    }
    { // time/disabled
        Form f("<form id=f><fieldset id=s><input id=c type=\"time\"  required disabled ></fieldset></form>");
        CHECK((f.at("#f:valid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#f:invalid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#s:valid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#s:invalid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#c:valid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#c:invalid") != WEVA_ELEMENT_NONE) == false);
    }
    { // time/readonly
        Form f("<form id=f><fieldset id=s><input id=c type=\"time\"   required readonly></fieldset></form>");
        CHECK((f.at("#f:valid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#f:invalid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#s:valid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#s:invalid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#c:valid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#c:invalid") != WEVA_ELEMENT_NONE) == false);
    }
    { // time/custom
        Form f("<form id=f><fieldset id=s><input id=c type=\"time\"   ></fieldset></form>");
        CHECK(weva_element_set_custom_validity(f.doc, f.at("#c"), "Reserved") == WEVA_OK);
        CHECK((f.at("#f:valid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#f:invalid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#s:valid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#s:invalid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#c:valid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#c:invalid") != WEVA_ELEMENT_NONE) == true);
    }
    { // month/plain
        Form f("<form id=f><fieldset id=s><input id=c type=\"month\"   ></fieldset></form>");
        CHECK((f.at("#f:valid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#f:invalid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#s:valid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#s:invalid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#c:valid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#c:invalid") != WEVA_ELEMENT_NONE) == false);
    }
    { // month/required
        Form f("<form id=f><fieldset id=s><input id=c type=\"month\" required  ></fieldset></form>");
        CHECK((f.at("#f:valid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#f:invalid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#s:valid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#s:invalid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#c:valid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#c:invalid") != WEVA_ELEMENT_NONE) == true);
    }
    { // month/disabled
        Form f("<form id=f><fieldset id=s><input id=c type=\"month\"  required disabled ></fieldset></form>");
        CHECK((f.at("#f:valid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#f:invalid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#s:valid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#s:invalid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#c:valid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#c:invalid") != WEVA_ELEMENT_NONE) == false);
    }
    { // month/readonly
        Form f("<form id=f><fieldset id=s><input id=c type=\"month\"   required readonly></fieldset></form>");
        CHECK((f.at("#f:valid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#f:invalid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#s:valid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#s:invalid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#c:valid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#c:invalid") != WEVA_ELEMENT_NONE) == false);
    }
    { // month/custom
        Form f("<form id=f><fieldset id=s><input id=c type=\"month\"   ></fieldset></form>");
        CHECK(weva_element_set_custom_validity(f.doc, f.at("#c"), "Reserved") == WEVA_OK);
        CHECK((f.at("#f:valid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#f:invalid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#s:valid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#s:invalid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#c:valid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#c:invalid") != WEVA_ELEMENT_NONE) == true);
    }
    { // week/plain
        Form f("<form id=f><fieldset id=s><input id=c type=\"week\"   ></fieldset></form>");
        CHECK((f.at("#f:valid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#f:invalid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#s:valid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#s:invalid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#c:valid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#c:invalid") != WEVA_ELEMENT_NONE) == false);
    }
    { // week/required
        Form f("<form id=f><fieldset id=s><input id=c type=\"week\" required  ></fieldset></form>");
        CHECK((f.at("#f:valid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#f:invalid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#s:valid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#s:invalid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#c:valid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#c:invalid") != WEVA_ELEMENT_NONE) == true);
    }
    { // week/disabled
        Form f("<form id=f><fieldset id=s><input id=c type=\"week\"  required disabled ></fieldset></form>");
        CHECK((f.at("#f:valid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#f:invalid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#s:valid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#s:invalid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#c:valid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#c:invalid") != WEVA_ELEMENT_NONE) == false);
    }
    { // week/readonly
        Form f("<form id=f><fieldset id=s><input id=c type=\"week\"   required readonly></fieldset></form>");
        CHECK((f.at("#f:valid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#f:invalid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#s:valid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#s:invalid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#c:valid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#c:invalid") != WEVA_ELEMENT_NONE) == false);
    }
    { // week/custom
        Form f("<form id=f><fieldset id=s><input id=c type=\"week\"   ></fieldset></form>");
        CHECK(weva_element_set_custom_validity(f.doc, f.at("#c"), "Reserved") == WEVA_OK);
        CHECK((f.at("#f:valid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#f:invalid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#s:valid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#s:invalid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#c:valid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#c:invalid") != WEVA_ELEMENT_NONE) == true);
    }
    { // datetime-local/plain
        Form f("<form id=f><fieldset id=s><input id=c type=\"datetime-local\"   ></fieldset></form>");
        CHECK((f.at("#f:valid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#f:invalid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#s:valid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#s:invalid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#c:valid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#c:invalid") != WEVA_ELEMENT_NONE) == false);
    }
    { // datetime-local/required
        Form f("<form id=f><fieldset id=s><input id=c type=\"datetime-local\" required  ></fieldset></form>");
        CHECK((f.at("#f:valid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#f:invalid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#s:valid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#s:invalid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#c:valid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#c:invalid") != WEVA_ELEMENT_NONE) == true);
    }
    { // datetime-local/disabled
        Form f("<form id=f><fieldset id=s><input id=c type=\"datetime-local\"  required disabled ></fieldset></form>");
        CHECK((f.at("#f:valid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#f:invalid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#s:valid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#s:invalid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#c:valid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#c:invalid") != WEVA_ELEMENT_NONE) == false);
    }
    { // datetime-local/readonly
        Form f("<form id=f><fieldset id=s><input id=c type=\"datetime-local\"   required readonly></fieldset></form>");
        CHECK((f.at("#f:valid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#f:invalid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#s:valid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#s:invalid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#c:valid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#c:invalid") != WEVA_ELEMENT_NONE) == false);
    }
    { // datetime-local/custom
        Form f("<form id=f><fieldset id=s><input id=c type=\"datetime-local\"   ></fieldset></form>");
        CHECK(weva_element_set_custom_validity(f.doc, f.at("#c"), "Reserved") == WEVA_OK);
        CHECK((f.at("#f:valid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#f:invalid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#s:valid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#s:invalid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#c:valid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#c:invalid") != WEVA_ELEMENT_NONE) == true);
    }
    { // hidden/plain
        Form f("<form id=f><fieldset id=s><input id=c type=\"hidden\"   ></fieldset></form>");
        CHECK((f.at("#f:valid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#f:invalid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#s:valid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#s:invalid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#c:valid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#c:invalid") != WEVA_ELEMENT_NONE) == false);
    }
    { // hidden/required
        Form f("<form id=f><fieldset id=s><input id=c type=\"hidden\" required  ></fieldset></form>");
        CHECK((f.at("#f:valid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#f:invalid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#s:valid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#s:invalid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#c:valid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#c:invalid") != WEVA_ELEMENT_NONE) == false);
    }
    { // hidden/disabled
        Form f("<form id=f><fieldset id=s><input id=c type=\"hidden\"  required disabled ></fieldset></form>");
        CHECK((f.at("#f:valid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#f:invalid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#s:valid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#s:invalid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#c:valid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#c:invalid") != WEVA_ELEMENT_NONE) == false);
    }
    { // hidden/readonly
        Form f("<form id=f><fieldset id=s><input id=c type=\"hidden\"   required readonly></fieldset></form>");
        CHECK((f.at("#f:valid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#f:invalid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#s:valid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#s:invalid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#c:valid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#c:invalid") != WEVA_ELEMENT_NONE) == false);
    }
    { // hidden/custom
        Form f("<form id=f><fieldset id=s><input id=c type=\"hidden\"   ></fieldset></form>");
        CHECK(weva_element_set_custom_validity(f.doc, f.at("#c"), "Reserved") == WEVA_OK);
        CHECK((f.at("#f:valid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#f:invalid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#s:valid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#s:invalid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#c:valid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#c:invalid") != WEVA_ELEMENT_NONE) == false);
    }
    { // button/plain
        Form f("<form id=f><fieldset id=s><input id=c type=\"button\"   ></fieldset></form>");
        CHECK((f.at("#f:valid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#f:invalid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#s:valid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#s:invalid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#c:valid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#c:invalid") != WEVA_ELEMENT_NONE) == false);
    }
    { // button/required
        Form f("<form id=f><fieldset id=s><input id=c type=\"button\" required  ></fieldset></form>");
        CHECK((f.at("#f:valid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#f:invalid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#s:valid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#s:invalid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#c:valid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#c:invalid") != WEVA_ELEMENT_NONE) == false);
    }
    { // button/disabled
        Form f("<form id=f><fieldset id=s><input id=c type=\"button\"  required disabled ></fieldset></form>");
        CHECK((f.at("#f:valid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#f:invalid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#s:valid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#s:invalid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#c:valid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#c:invalid") != WEVA_ELEMENT_NONE) == false);
    }
    { // button/readonly
        Form f("<form id=f><fieldset id=s><input id=c type=\"button\"   required readonly></fieldset></form>");
        CHECK((f.at("#f:valid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#f:invalid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#s:valid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#s:invalid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#c:valid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#c:invalid") != WEVA_ELEMENT_NONE) == false);
    }
    { // button/custom
        Form f("<form id=f><fieldset id=s><input id=c type=\"button\"   ></fieldset></form>");
        CHECK(weva_element_set_custom_validity(f.doc, f.at("#c"), "Reserved") == WEVA_OK);
        CHECK((f.at("#f:valid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#f:invalid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#s:valid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#s:invalid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#c:valid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#c:invalid") != WEVA_ELEMENT_NONE) == false);
    }
    { // submit/plain
        Form f("<form id=f><fieldset id=s><input id=c type=\"submit\"   ></fieldset></form>");
        CHECK((f.at("#f:valid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#f:invalid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#s:valid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#s:invalid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#c:valid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#c:invalid") != WEVA_ELEMENT_NONE) == false);
    }
    { // submit/required
        Form f("<form id=f><fieldset id=s><input id=c type=\"submit\" required  ></fieldset></form>");
        CHECK((f.at("#f:valid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#f:invalid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#s:valid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#s:invalid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#c:valid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#c:invalid") != WEVA_ELEMENT_NONE) == false);
    }
    { // submit/disabled
        Form f("<form id=f><fieldset id=s><input id=c type=\"submit\"  required disabled ></fieldset></form>");
        CHECK((f.at("#f:valid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#f:invalid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#s:valid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#s:invalid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#c:valid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#c:invalid") != WEVA_ELEMENT_NONE) == false);
    }
    { // submit/readonly
        Form f("<form id=f><fieldset id=s><input id=c type=\"submit\"   required readonly></fieldset></form>");
        CHECK((f.at("#f:valid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#f:invalid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#s:valid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#s:invalid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#c:valid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#c:invalid") != WEVA_ELEMENT_NONE) == false);
    }
    { // submit/custom
        Form f("<form id=f><fieldset id=s><input id=c type=\"submit\"   ></fieldset></form>");
        CHECK(weva_element_set_custom_validity(f.doc, f.at("#c"), "Reserved") == WEVA_OK);
        CHECK((f.at("#f:valid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#f:invalid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#s:valid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#s:invalid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#c:valid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#c:invalid") != WEVA_ELEMENT_NONE) == true);
    }
    { // reset/plain
        Form f("<form id=f><fieldset id=s><input id=c type=\"reset\"   ></fieldset></form>");
        CHECK((f.at("#f:valid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#f:invalid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#s:valid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#s:invalid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#c:valid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#c:invalid") != WEVA_ELEMENT_NONE) == false);
    }
    { // reset/required
        Form f("<form id=f><fieldset id=s><input id=c type=\"reset\" required  ></fieldset></form>");
        CHECK((f.at("#f:valid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#f:invalid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#s:valid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#s:invalid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#c:valid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#c:invalid") != WEVA_ELEMENT_NONE) == false);
    }
    { // reset/disabled
        Form f("<form id=f><fieldset id=s><input id=c type=\"reset\"  required disabled ></fieldset></form>");
        CHECK((f.at("#f:valid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#f:invalid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#s:valid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#s:invalid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#c:valid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#c:invalid") != WEVA_ELEMENT_NONE) == false);
    }
    { // reset/readonly
        Form f("<form id=f><fieldset id=s><input id=c type=\"reset\"   required readonly></fieldset></form>");
        CHECK((f.at("#f:valid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#f:invalid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#s:valid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#s:invalid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#c:valid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#c:invalid") != WEVA_ELEMENT_NONE) == false);
    }
    { // reset/custom
        Form f("<form id=f><fieldset id=s><input id=c type=\"reset\"   ></fieldset></form>");
        CHECK(weva_element_set_custom_validity(f.doc, f.at("#c"), "Reserved") == WEVA_OK);
        CHECK((f.at("#f:valid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#f:invalid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#s:valid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#s:invalid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#c:valid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#c:invalid") != WEVA_ELEMENT_NONE) == false);
    }
    { // file/plain
        Form f("<form id=f><fieldset id=s><input id=c type=\"file\"   ></fieldset></form>");
        CHECK((f.at("#f:valid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#f:invalid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#s:valid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#s:invalid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#c:valid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#c:invalid") != WEVA_ELEMENT_NONE) == false);
    }
    { // file/required
        Form f("<form id=f><fieldset id=s><input id=c type=\"file\" required  ></fieldset></form>");
        CHECK((f.at("#f:valid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#f:invalid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#s:valid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#s:invalid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#c:valid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#c:invalid") != WEVA_ELEMENT_NONE) == true);
    }
    { // file/disabled
        Form f("<form id=f><fieldset id=s><input id=c type=\"file\"  required disabled ></fieldset></form>");
        CHECK((f.at("#f:valid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#f:invalid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#s:valid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#s:invalid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#c:valid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#c:invalid") != WEVA_ELEMENT_NONE) == false);
    }
    { // file/readonly
        Form f("<form id=f><fieldset id=s><input id=c type=\"file\"   required readonly></fieldset></form>");
        CHECK((f.at("#f:valid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#f:invalid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#s:valid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#s:invalid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#c:valid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#c:invalid") != WEVA_ELEMENT_NONE) == false);
    }
    { // file/custom
        Form f("<form id=f><fieldset id=s><input id=c type=\"file\"   ></fieldset></form>");
        CHECK(weva_element_set_custom_validity(f.doc, f.at("#c"), "Reserved") == WEVA_OK);
        CHECK((f.at("#f:valid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#f:invalid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#s:valid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#s:invalid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#c:valid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#c:invalid") != WEVA_ELEMENT_NONE) == true);
    }
    { // color/plain
        Form f("<form id=f><fieldset id=s><input id=c type=\"color\"   ></fieldset></form>");
        CHECK((f.at("#f:valid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#f:invalid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#s:valid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#s:invalid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#c:valid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#c:invalid") != WEVA_ELEMENT_NONE) == false);
    }
    { // color/required
        Form f("<form id=f><fieldset id=s><input id=c type=\"color\" required  ></fieldset></form>");
        CHECK((f.at("#f:valid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#f:invalid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#s:valid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#s:invalid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#c:valid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#c:invalid") != WEVA_ELEMENT_NONE) == false);
    }
    { // color/disabled
        Form f("<form id=f><fieldset id=s><input id=c type=\"color\"  required disabled ></fieldset></form>");
        CHECK((f.at("#f:valid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#f:invalid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#s:valid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#s:invalid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#c:valid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#c:invalid") != WEVA_ELEMENT_NONE) == false);
    }
    { // color/readonly
        Form f("<form id=f><fieldset id=s><input id=c type=\"color\"   required readonly></fieldset></form>");
        CHECK((f.at("#f:valid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#f:invalid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#s:valid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#s:invalid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#c:valid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#c:invalid") != WEVA_ELEMENT_NONE) == false);
    }
    { // color/custom
        Form f("<form id=f><fieldset id=s><input id=c type=\"color\"   ></fieldset></form>");
        CHECK(weva_element_set_custom_validity(f.doc, f.at("#c"), "Reserved") == WEVA_OK);
        CHECK((f.at("#f:valid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#f:invalid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#s:valid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#s:invalid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#c:valid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#c:invalid") != WEVA_ELEMENT_NONE) == true);
    }
    { // empty
        Form f("<form id=f><fieldset id=s></fieldset></form>");
        CHECK((f.at("#f:valid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#f:invalid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#s:valid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#s:invalid") != WEVA_ELEMENT_NONE) == false);
    }
    { // external-owner
        Form f("<form id=f></form><fieldset id=s><input id=c required form=f></fieldset>");
        CHECK((f.at("#f:valid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#f:invalid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#s:valid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#s:invalid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#c:valid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#c:invalid") != WEVA_ELEMENT_NONE) == true);
    }
    { // other-owner
        Form f("<form id=other></form><form id=f><fieldset id=s><input id=c required form=other></fieldset></form>");
        CHECK((f.at("#f:valid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#f:invalid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#s:valid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#s:invalid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#c:valid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#c:invalid") != WEVA_ELEMENT_NONE) == true);
    }
    { // fieldset-disabled
        Form f("<form id=f><fieldset id=s disabled><input id=c required></fieldset></form>");
        CHECK((f.at("#f:valid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#f:invalid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#s:valid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#s:invalid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#c:valid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#c:invalid") != WEVA_ELEMENT_NONE) == false);
    }
    { // first-legend
        Form f("<form id=f><fieldset id=s disabled><legend><input id=c required></legend></fieldset></form>");
        CHECK((f.at("#f:valid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#f:invalid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#s:valid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#s:invalid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#c:valid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#c:invalid") != WEVA_ELEMENT_NONE) == true);
    }
    { // datalist
        Form f("<form id=f><fieldset id=s><datalist><input id=c required></datalist></fieldset></form>");
        CHECK((f.at("#f:valid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#f:invalid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#s:valid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#s:invalid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#c:valid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#c:invalid") != WEVA_ELEMENT_NONE) == false);
    }
    { // readonly-parent
        Form f("<form id=f readonly><fieldset id=s readonly><input id=c required></fieldset></form>");
        CHECK((f.at("#f:valid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#f:invalid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#s:valid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#s:invalid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#c:valid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#c:invalid") != WEVA_ELEMENT_NONE) == true);
    }
    { // fieldset-custom
        Form f("<form id=f><fieldset id=s><input id=c></fieldset></form>");
        CHECK(weva_element_set_custom_validity(f.doc, f.at("#s"), "Reserved") == WEVA_OK);
        CHECK((f.at("#f:valid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#f:invalid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#s:valid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#s:invalid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#c:valid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#c:invalid") != WEVA_ELEMENT_NONE) == false);
    }
    { // button-custom
        Form f("<form id=f><fieldset id=s><button id=c>Save</button></fieldset></form>");
        CHECK(weva_element_set_custom_validity(f.doc, f.at("#c"), "Reserved") == WEVA_OK);
        CHECK((f.at("#f:valid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#f:invalid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#s:valid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#s:invalid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#c:valid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#c:invalid") != WEVA_ELEMENT_NONE) == true);
    }
    { // textarea
        Form f("<form id=f><fieldset id=s><textarea id=c required></textarea></fieldset></form>");
        CHECK((f.at("#f:valid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#f:invalid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#s:valid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#s:invalid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#c:valid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#c:invalid") != WEVA_ELEMENT_NONE) == true);
    }
    { // select
        Form f("<form id=f><fieldset id=s><select id=c required><option value=\"\">Choose</option></select></fieldset></form>");
        CHECK((f.at("#f:valid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#f:invalid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#s:valid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#s:invalid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#c:valid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#c:invalid") != WEVA_ELEMENT_NONE) == true);
    }
    { // number-step
        Form f("<form id=f><fieldset id=s><input id=c type=number min=2 step=2 value=5></fieldset></form>");
        CHECK((f.at("#f:valid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#f:invalid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#s:valid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#s:invalid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#c:valid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#c:invalid") != WEVA_ELEMENT_NONE) == true);
    }
    { // pattern
        Form f("<form id=f><fieldset id=s><input id=c pattern=\"[a-z]+\" value=\"123\"></fieldset></form>");
        CHECK((f.at("#f:valid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#f:invalid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#s:valid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#s:invalid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#c:valid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#c:invalid") != WEVA_ELEMENT_NONE) == false);
    }
    { // novalidate
        Form f("<form id=f novalidate><fieldset id=s><input id=c required></fieldset></form>");
        CHECK((f.at("#f:valid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#f:invalid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#s:valid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#s:invalid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#c:valid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#c:invalid") != WEVA_ELEMENT_NONE) == true);
    }
    { // radio-group
        Form f("<form id=f><fieldset id=s><input id=c type=radio name=g><input type=radio name=g required></fieldset></form>");
        CHECK((f.at("#f:valid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#f:invalid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#s:valid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#s:invalid") != WEVA_ELEMENT_NONE) == true);
        CHECK((f.at("#c:valid") != WEVA_ELEMENT_NONE) == false);
        CHECK((f.at("#c:invalid") != WEVA_ELEMENT_NONE) == true);
    }
    Form f("<form id=f></form><fieldset id=s><input id=c form=f required></fieldset>");
    const char* css = "form,fieldset{width:100px;min-width:0;box-sizing:border-box;padding:0;border:0}form:invalid{width:120px}fieldset:invalid{width:140px}input{width:100px;min-width:0;box-sizing:border-box}input:is(:invalid){width:160px}fieldset:valid input{width:180px}";
    CHECK(weva_document_set_css(f.doc, css, std::strlen(css)) == WEVA_OK);
    auto width = [&](const char* selector) {
        CHECK(weva_document_update(f.doc, 0) == WEVA_OK);
        double x,y,w,h;
        CHECK(weva_element_bounds(f.doc, f.at(selector), &x,&y,&w,&h) == WEVA_OK);
        return w;
    };
    CHECK(width("#f") == 120); CHECK(width("#s") == 140); CHECK(width("#c") == 160);
    f.set("#c", "ready");
    CHECK(width("#f") == 100); CHECK(width("#s") == 100); CHECK(width("#c") == 180);
    f.set("#c", "still ready"); CHECK(width("#c") == 180);
    CHECK(weva_element_set_custom_validity(f.doc, f.at("#c"), "Reserved") == WEVA_OK);
    CHECK(width("#f") == 120); CHECK(width("#s") == 140); CHECK(width("#c") == 160);
    CHECK(weva_element_set_custom_validity(f.doc, f.at("#c"), "") == WEVA_OK);
    CHECK(width("#f") == 100); CHECK(width("#s") == 100); CHECK(width("#c") == 180);
    f.set("#c", ""); f.set("#c", "queued"); CHECK(width("#f") == 100);
    f.set("#c", ""); CHECK(width("#f") == 120);
    f.attr("#c", "form", "missing"); CHECK(width("#f") == 100); CHECK(width("#s") == 140);
    f.attr("#s", "disabled", ""); CHECK(width("#s") == 100);
}
