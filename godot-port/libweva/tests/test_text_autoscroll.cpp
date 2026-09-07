#include "text_autoscroll_fixture.h"
#include "weva/grapheme.h"
#include <limits>

namespace {
bool caret_boundary(const std::string& value,int index) {
    size_t offset=0;
    weva::Graphemes graphemes(value,weva::GraphemeProfile::BrowserCaret);
    while(offset<static_cast<size_t>(index) && graphemes.next(&offset)) {}
    return offset==static_cast<size_t>(index);
}
}
void test_text_autoscroll() {
    for(const char* kind : {"text","password","textarea"}) {
        TextScrollDoc d(kind);
        const bool area=std::strcmp(kind,"textarea")==0;
        d.start(area?2:0);
        const auto initial=d.selection();
        const auto clock=d.bounds("#clock");
        CHECK(weva_document_needs_input_tick(d.doc));CHECK(d.events().empty());
        for(int i=0;i<60;++i){
            d.update(0.05);const auto selected=d.selection();
            CHECK(selected.first==initial.first);CHECK(caret_boundary(d.value,selected.second));
        }
        CHECK(d.selection().second==static_cast<int>(d.value.size()));
        CHECK(d.selection().second>initial.second);
        CHECK(d.events().empty());CHECK(d.scroll_events>0);
        CHECK(d.scroll_event_x>0);if(area)CHECK(d.scroll_event_y>0);
        CHECK(d.bounds("#clock").x==clock.x);
        const auto serial=weva_document_draw_serial(d.doc);const auto n=d.scroll_events;
        d.update(0.1);CHECK(weva_document_draw_serial(d.doc)==serial);
        CHECK(d.events().empty());CHECK(d.scroll_events==n);
        auto b=d.bounds();d.pointer(b.x-40,b.y-40);
        for(int i=0;i<60;++i)d.update(0.05);
        CHECK(d.selection().first==initial.first);CHECK(d.selection().second==0);
        d.pointer(b.x-40,b.y-40,0);CHECK(!weva_document_needs_input_tick(d.doc));
        const auto stopped=d.selection();const auto stopped_scroll=d.scroll();
        d.update(0.3);CHECK(d.selection()==stopped);CHECK(d.scroll()==stopped_scroll);
        CHECK(d.events().empty());
        d.update(0,0.1);CHECK(d.bounds("#clock").x>clock.x);
    }
    TextScrollDoc unarmed;
    auto b=unarmed.bounds();unarmed.pointer(b.x+20,b.y+16);unarmed.pointer(b.x+b.w+40,b.y+16);
    const auto original=unarmed.selection();unarmed.update(0.1);
    CHECK(!weva_document_needs_input_tick(unarmed.doc));CHECK(unarmed.selection()==original);
    CHECK(unarmed.events().empty());CHECK(unarmed.scroll_events==0);

    TextScrollDoc middle;
    middle.start();middle.update(0.05);middle.events();
    b=middle.bounds();middle.pointer(b.x+b.w/2,b.y+16);
    const auto selected=middle.selection();middle.update(0.2);
    CHECK(middle.selection()==selected);CHECK(middle.events().empty());
    middle.pointer(b.x+b.w+40,b.y+16);middle.update(0.05);
    const auto scroll_count=middle.scroll_events;middle.events();
    CHECK(middle.scroll_events>scroll_count);
    // Releasing mid-scroll must retain the viewport even if the selection's
    // moving end is beyond the clipped field.
    double cx=0,cy=0;
    CHECK(weva_document_caret_bounds(middle.doc,&cx,&cy,nullptr,nullptr));
    middle.pointer(b.x+b.w+40,b.y+16,0);
    double after=0;CHECK(weva_document_caret_bounds(middle.doc,&after,nullptr,nullptr,nullptr));
    CHECK(after==cx);CHECK(middle.events().empty());

    TextScrollDoc wrapped("textarea",true);
    wrapped.start(1);const auto from=wrapped.selection();
    for(int i=0;i<30;++i)wrapped.update(0.05);
    CHECK(wrapped.scroll().second>0);CHECK(wrapped.selection().first==from.first);
    CHECK(wrapped.selection().second>from.second);CHECK(caret_boundary(wrapped.value,wrapped.selection().second));

    TextScrollDoc readonly("textarea");
    weva_element_set_attribute(readonly.doc,readonly.at("#f"),"readonly","");readonly.update();readonly.start(1);
    readonly.update(0.1);CHECK(readonly.scroll().second>0);CHECK(readonly.events().empty());
}

void test_text_autoscroll_lifecycle() {
    for(const char* kind : {"text","textarea"})for(int action=0;action<7;++action){
        TextScrollDoc d(kind);d.start(std::strcmp(kind,"textarea")==0?2:0);d.update(0.05);
        CHECK(weva_document_needs_input_tick(d.doc));
        switch(action){
            case 0:weva_document_clear_pointer(d.doc);break;
            case 1:weva_document_set_focus(d.doc,d.at("#other"));break;
            case 2:weva_element_set_attribute(d.doc,d.at("#f"),"disabled","");break;
            case 3:weva_element_set_attribute(d.doc,d.at("#outer"),"style","visibility:hidden");break;
            case 4:weva_element_remove(d.doc,d.at("#outer"));break;
            case 5:weva_document_reset_form(d.doc,d.at("#form"));break;
            case 6:weva_document_load_html(d.doc,d.html.data(),d.html.size());break;
        }
        d.update(0.1);CHECK(!weva_document_needs_input_tick(d.doc));CHECK(d.events().empty());
        for(int i=0;i<10;++i)d.update(0.05);
        CHECK(d.events().empty());
    }
    TextScrollDoc invalid;invalid.start();const auto old=invalid.selection();
    for(double dt : {0.0,-1.0,std::numeric_limits<double>::infinity(),std::numeric_limits<double>::quiet_NaN()})invalid.update(dt);
    CHECK(invalid.selection()==old);
}
