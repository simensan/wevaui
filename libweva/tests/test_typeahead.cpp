#include "check.h"
#include "weva/typeahead.h"
#include "weva/form_state.h"
#include "weva_c.h"
#include <cstring>
#include <thread>
#include <chrono>
#include <vector>
using namespace weva;

void test_unicode_typeahead() {
    const struct { const char* prefix; const char* label; bool matches; } cases[] = {
        {"e", "Éclair", true}, {"é", "Eclair", true}, {"é", "e\u0301clair", true},
        {"a", "Åland", true}, {"o", "Örebro", true}, {"ae", "Æther", true},
        {"a", "Æther", false}, {"s", "ßeta", false}, {"ss", "ßeta", true},
        {"strasse", "Straße", true}, {"oe", "Œuvre", true}, {"i", "İzmir", true},
        {"i", "ıstanbul", false}, {"lodz", "Łódź", true}, {"o", "Øresund", true},
        {"a", "Ａlpha", true}, {"ffi", "ﬃeld", true}, {"f", "ﬃeld", false},
        {"и", "Йога", false}, {"е", "Ёлка", true}, {"σ", "Σίγμα", true},
        {"か", "カナ", true}, {"ば", "はな", true}, {"한", "한글", true},
        {"b", "\u00a0\u2003Bravo", true}, {"b", "Alpha Bravo", false},
        {"blue b", "Blue bird", true}, {"", "Alpha", false}, {"a", "", false},
        {"😀", "😀 smile", true},
    };
    for (const auto& c : cases) {
        UnicodePrefixSearch search(c.prefix);
        CHECK(search.valid() == (*c.prefix != 0));
        CHECK(search.matches(c.label) == c.matches);
        CHECK(!search.matches("unrelated"));
        CHECK(search.matches(c.label) == c.matches); // reused UTF-16 buffers remain valid
    }
    TypeAheadSession session;
    CHECK(session.append("b", 1)); CHECK_EQ(std::string(session.prefix()), "b");
    CHECK(session.append("b", 1.1)); CHECK_EQ(std::string(session.prefix()), "b");
    CHECK(!session.append("l", 1.2)); CHECK_EQ(std::string(session.prefix()), "bbl");
    CHECK(session.active(2.199)); CHECK(!session.active(2.201));
    CHECK(session.append("a", 2.3)); CHECK_EQ(std::string(session.prefix()), "a");
    CHECK(!session.append("l", 2.4)); CHECK_EQ(std::string(session.prefix()), "al");
    CHECK(!session.append("p", 2.5)); CHECK_EQ(std::string(session.prefix()), "alp");
    session.reset(); CHECK(!session.active(2.6));
    CHECK(session.append("é", 2.7)); CHECK(session.append("é", 2.8));
    CHECK_EQ(std::string(session.prefix()), "é");
    CHECK(typeahead_printable("é")); CHECK(typeahead_printable("😀"));
    CHECK(!typeahead_printable("\n")); CHECK(!typeahead_printable("\x7f"));
    CHECK(!typeahead_printable("\xFF")); CHECK(!typeahead_printable("ab"));
}

namespace {
struct TypeDoc {
    weva_document_t doc;
    explicit TypeDoc(int mode) {
        weva_config c{}; c.viewport_width = 500; c.viewport_height = 400; c.use_user_agent_stylesheet = 1;
        doc = weva_document_create(&c);
        const char* css = "html,body{margin:0}select{display:block;width:260px;height:34px}select[size]{height:240px}option{height:24px}";
        weva_document_add_css(doc, css, std::strlen(css));
        const std::string html = "<form id=f><select id=s" + std::string(mode == 1 ? " size=6" : mode == 2 ? " multiple size=6" : "") + ">"
            "<option value=a>Alpha</option><option value=al>Alpine</option>"
            "<option id=beta value=b label=Beta>Submitted beta text</option>"
            "<option value=br>Bravo</option><option value=bl>Blue bird</option>"
            "<option value=bu>Bumblebee</option><option id=cherry value=c>Cherry</option>"
            "<option value=bad disabled>Bear</option><optgroup id=group label=Disabled disabled>"
            "<option value=bad2>Banana</option></optgroup></select>"
            "<button id=other type=button>Other</button><input id=text></form>";
        weva_document_load_html(doc, html.data(), html.size());
        update(); focus(); events();
    }
    ~TypeDoc() { weva_document_destroy(doc); }
    weva_element_t at(const char* selector) { return weva_document_query(doc, selector); }
    void update() { CHECK(weva_document_update(doc, 0) == WEVA_OK); }
    void focus() { weva_document_set_focus(doc, at("#other")); weva_document_set_focus(doc, at("#s")); update(); }
    void type(const char* text, int consumed=1, uint32_t modifiers=0) {
        CHECK(weva_document_try_text_input_modifiers(doc, text, modifiers) == consumed); update();
    }
    std::string value() { char out[256]{}; weva_element_value(doc, at("#s"), out, sizeof(out)); return out; }
    std::vector<int> events() {
        std::vector<int> out; weva_event event{};
        while (weva_document_poll_event(doc, &event))
            if (event.kind == WEVA_EVENT_VALUE_CHANGED || event.kind == WEVA_EVENT_CHANGE) {
                CHECK(event.target == at("#s")); out.push_back(event.kind);
            }
        return out;
    }
    void check(const char* expected, int mode, int changes=1) {
        CHECK_EQ(value(), expected);
        std::vector<int> want;
        for (int i=0; i<changes; ++i) {
            if (mode == 0) want.push_back(WEVA_EVENT_VALUE_CHANGED);
            want.push_back(WEVA_EVENT_CHANGE);
        }
        const auto actual = events();
        if (actual != want) {
            std::fprintf(stderr, "typeahead mode=%d value=%s: events", mode, expected);
            for (auto event : actual) std::fprintf(stderr, " %d", event);
            std::fprintf(stderr, "; expected");
            for (auto event : want) std::fprintf(stderr, " %d", event);
            std::fprintf(stderr, "\n");
        }
        CHECK(actual == want);
    }
};
}
void test_select_typeahead_actions() {
    for (int mode : {0,1,2}) {
        TypeDoc d(mode);
        d.type("b"); d.check("b",mode);
        d.type("b"); d.check("br",mode);
        d.type("l"); d.check("br",mode,0); // bbl is retained, not guessed as a new prefix
        d.type("x"); d.check("br",mode,0);
        d.focus(); d.events();
        d.type("a"); d.check("a",mode);
        d.type("l"); d.check("a",mode,mode == 0 ? 0 : 1);
        d.type("p"); d.check("a",mode,mode == 0 ? 0 : 1);
        d.type("i"); d.check("al",mode);
        d.focus(); d.events();
        d.type("blue"); d.check("bl",mode,mode == 0 ? 2 : 4);
        CHECK(weva_document_key(d.doc, WEVA_KEY_SPACE, 0, 1) == 1);
        weva_document_key(d.doc, WEVA_KEY_SPACE, 0, 0); d.update();
        d.check("bl",mode,mode == 0 ? 0 : 1);
        CHECK(weva_document_open_select_element(d.doc) == WEVA_ELEMENT_NONE);
        d.type("bi"); d.check("bl",mode,mode == 0 ? 0 : 2);
        d.focus(); d.events();
        d.type("b",0,WEVA_MOD_CTRL); d.check("bl",mode,0);
        d.type("b",0,WEVA_MOD_ALT); d.check("bl",mode,0);
        d.type("b",0,WEVA_MOD_META); d.check("bl",mode,0);
        d.type("B",1,WEVA_MOD_SHIFT); d.check("bu",mode);
        d.type("B",1,WEVA_MOD_SHIFT); d.check("b",mode); // disabled choices skipped on wrap
        CHECK(weva_document_paste_text(d.doc, "Cherry") == 0); d.check("b",mode,0);
        const auto serial = weva_document_draw_serial(d.doc);
        for (int i=0; i<50; ++i) d.update();
        CHECK(weva_document_draw_serial(d.doc) == serial);
        d.focus(); d.events();
        CHECK(weva_element_set_attribute(d.doc, d.at("#beta"), "label", "Delta") == WEVA_OK); d.update();
        d.type("d"); d.check("b",mode,mode == 0 ? 0 : 1);
        CHECK(weva_element_set_attribute(d.doc, d.at("#s"), "disabled", "") == WEVA_OK); d.update();
        d.type("a",0); d.check("b",mode,0);
    }
    TypeDoc timeout(0);
    timeout.type("x"); timeout.check("a",0,0);
    // Native input time continues while document animation time is paused.
    std::this_thread::sleep_for(std::chrono::milliseconds(1100));
    timeout.type("c"); timeout.check("c",0);
    timeout.focus(); timeout.events();
    CHECK(weva_document_open_select(timeout.doc, timeout.at("#s")) == 1);
    timeout.type("b"); timeout.check("c",0,0); // an open popup defers the choice
    CHECK(weva_document_key(timeout.doc, WEVA_KEY_ENTER, 0, 1) == 1);
    weva_document_key(timeout.doc, WEVA_KEY_ENTER, 0, 0); timeout.update();
    timeout.check("b",0);
}

void test_select_labels() {
    auto option = make_ref<Element>("option");
    auto span = make_ref<Element>("span");
    auto text = make_ref<TextNode>("  nested\n text ");
    auto script = make_ref<Element>("script");
    auto ignored = make_ref<TextNode>("ignored");
    span->append_child(text.get()); script->append_child(ignored.get());
    option->append_child(span.get()); option->append_child(script.get());
    CHECK_EQ(option_value(*option), "nested text");
    CHECK_EQ(option_label(*option), "nested text");
    option->set_attribute("label", " Display  label ");
    CHECK_EQ(option_label(*option), " Display  label ");
    CHECK_EQ(option_value(*option), "nested text");
    const auto version = option->form_label_version();
    text->set_data("Updated");
    CHECK(option->form_label_version() > version);
    option->set_attribute("label", "");
    CHECK_EQ(option_label(*option), "Updated");

    for (int mode : {0,1,2}) {
        TypeDoc d(mode);
        CHECK(weva_element_set_value(d.doc, d.at("#s"), "b") == WEVA_OK); d.update();
        auto geometry = [&] {
            size_t count = 0; const auto* draws = weva_document_draws(d.doc, &count);
            std::string out;
            for (size_t i=0;i<count;++i) {
                const auto& draw = draws[i];
                out.append(reinterpret_cast<const char*>(&draw.kind), sizeof(draw.kind));
                out.append(reinterpret_cast<const char*>(draw.vertices), draw.vertex_count * sizeof(weva_vertex));
                out.append(reinterpret_cast<const char*>(draw.indices), draw.index_count * sizeof(uint32_t));
                out.append(reinterpret_cast<const char*>(&draw.scissor_x), 5 * sizeof(int32_t));
            }
            return out;
        };
        auto complete_matches = [&] {
            const auto retained = geometry();
            weva_document_set_viewport(d.doc, 501, 400);
            weva_document_set_viewport(d.doc, 500, 400); d.update();
            CHECK(geometry() == retained);
        };
        auto before = geometry();
        CHECK(weva_element_set_attribute(d.doc, d.at("#beta"), "label", "A much longer label") == WEVA_OK); d.update();
        CHECK(geometry() != before); complete_matches();
        CHECK(weva_element_set_attribute(d.doc, d.at("#beta"), "label", "") == WEVA_OK); d.update();
        complete_matches();
        before = geometry();
        CHECK(weva_element_set_text(d.doc, d.at("#beta"), "Changed option text") == WEVA_OK); d.update();
        CHECK(geometry() != before); complete_matches();
        if (mode == 0) {
            CHECK(weva_document_open_select(d.doc, d.at("#s")) == 1); d.update();
        }
        before = geometry();
        CHECK(weva_element_set_attribute(d.doc, d.at("#group"), "label", "Changed heading") == WEVA_OK); d.update();
        CHECK(geometry() != before); complete_matches();
        CHECK(weva_element_set_attribute(d.doc, d.at("#beta"), "label", "Updated while open") == WEVA_OK); d.update();
        complete_matches();
        if (mode != 0) {
            double gy=0,oy=0,h=0;
            CHECK(weva_element_bounds(d.doc,d.at("#group"),nullptr,&gy,nullptr,&h) == WEVA_OK);
            CHECK(weva_element_bounds(d.doc,d.at("#group option"),nullptr,&oy,nullptr,nullptr) == WEVA_OK);
            CHECK(oy > gy); CHECK(h > 24);
        }
        d.events();
        d.focus(); d.type("u");
        // Label search preserves the explicit submitted value across every mode.
        d.check("b",mode,mode == 0 ? 0 : 1);
    }
}
