#include "select_autoscroll_fixture.h"
#include <limits>

void test_select_autoscroll() {
    for(bool multiple : {false,true}) {
        AutoScrollDoc d(multiple);
        d.start();
        const auto initial=d.selected();
        CHECK(initial==(multiple ? std::vector<int>{1,2} : std::vector<int>{2}));
        CHECK(weva_document_needs_input_tick(d.doc));
        CHECK(d.events().empty());
        const auto clock=d.bounds("#clock");
        d.update(0.05);
        CHECK(d.scroll()>0);
        for(int i=0;i<30;++i) d.update(0.05);
        double maximum=0; const double bottom=d.scroll(&maximum);
        CHECK(maximum>300 && bottom==maximum);
        CHECK(d.selected()==initial); // A stationary pointer doesn't choose newly exposed rows.
        CHECK(d.events().empty());
        CHECK(d.scroll_events>0);
        CHECK(d.bounds("#clock").x==clock.x); // Input keeps working with CSS time paused.
        const auto serial=weva_document_draw_serial(d.doc);
        const auto scroll_events=d.scroll_events;
        d.update(0.1);
        CHECK(weva_document_draw_serial(d.doc)==serial);
        CHECK(d.events().empty()); CHECK(d.scroll_events==scroll_events);
        d.row(18);
        std::vector<int> expected;
        if(multiple) for(int i=1;i<=18;++i) {if(i!=5) expected.push_back(i);}
        else expected.push_back(18);
        CHECK(d.selected()==expected);
        CHECK(d.events().empty());
        d.below(0);
        CHECK(d.events()==(std::vector<int>{WEVA_EVENT_VALUE_CHANGED,WEVA_EVENT_CHANGE}));
        CHECK(!weva_document_needs_input_tick(d.doc));
        d.update(0.2);
        CHECK(d.scroll()==bottom);
        CHECK(d.events().empty());
        d.update(0,0.1);
        CHECK(d.bounds("#clock").x>clock.x);
    }

    AutoScrollDoc d;
    d.row(1); d.below(); // Moving directly outside doesn't arm the gesture.
    d.update(0.1);
    CHECK(!weva_document_needs_input_tick(d.doc)); CHECK(d.scroll()==0);
    d.row(2);
    auto b=d.bounds("#s");
    d.pointer(b.x+b.w+50,b.y+b.h/2);
    d.update(0.1); CHECK(d.scroll()==0); // Lateral escape alone has no vertical direction.
    d.pointer(b.x+b.w+50,b.y+b.h+30);
    d.update(0.1); CHECK(d.scroll()>0);
    d.pointer(b.x+b.w/2,b.y+b.h/2);
    const double center=d.scroll();
    d.update(0.1); CHECK(d.scroll()==center);
    d.pointer(b.x+b.w/2,b.y-30);
    for(int i=0;i<20;++i) d.update(0.05);
    CHECK(d.scroll()==0);
    weva_document_clear_pointer(d.doc); d.update();
    CHECK(!weva_document_needs_input_tick(d.doc));
    CHECK(d.events()==(std::vector<int>{WEVA_EVENT_VALUE_CHANGED,WEVA_EVENT_CHANGE}));
    const auto serial=weva_document_draw_serial(d.doc);
    for(int i=0;i<50;++i) d.update(0.02);
    CHECK(weva_document_draw_serial(d.doc)==serial);

    AutoScrollDoc toggle;
    weva_element_set_value(toggle.doc,toggle.at("#s"),"1,2,12"); toggle.update(); toggle.events();
    toggle.start(WEVA_MOD_CTRL);
    CHECK(toggle.selected()==std::vector<int>{12});
    toggle.update(0.1);
    CHECK(toggle.selected()==std::vector<int>{12});
    toggle.below(0);
    CHECK(toggle.events()==(std::vector<int>{WEVA_EVENT_VALUE_CHANGED,WEVA_EVENT_CHANGE}));

    AutoScrollDoc legacy;
    legacy.start();
    CHECK(weva_document_update(legacy.doc,0.05)==WEVA_OK);
    CHECK(legacy.scroll()>0); CHECK(legacy.bounds("#clock").x>0);
    const double from=legacy.scroll();
    for(double invalid : {0.0,-1.0,std::numeric_limits<double>::infinity(),std::numeric_limits<double>::quiet_NaN()})
        legacy.update(invalid);
    CHECK(legacy.scroll()==from);
}

void test_select_autoscroll_lifecycle() {
    for(int action=0;action<7;++action) {
        AutoScrollDoc d;
        d.start(); d.update(0.05);
        CHECK(d.scroll()>0);
        switch(action) {
            case 0: weva_element_set_attribute(d.doc,d.at("#s"),"disabled",""); break;
            case 1: weva_element_set_attribute(d.doc,d.at("#s"),"style","display:none"); break;
            case 2: weva_element_set_attribute(d.doc,d.at("#outer"),"style","visibility:hidden"); break;
            case 3: weva_element_remove(d.doc,d.at("#o1")); break;
            case 4: weva_element_remove(d.doc,d.at("#outer")); break;
            case 5: weva_document_reset_form(d.doc,d.at("#f")); break;
            case 6: weva_document_load_html(d.doc,d.html.data(),d.html.size()); break;
        }
        d.update(0.05);
        CHECK(!weva_document_needs_input_tick(d.doc));
        d.events();
        for(int i=0;i<5;++i) d.update(0.05);
        CHECK(d.events().empty());
        weva_document_clear_pointer(d.doc); d.update();
    }
    AutoScrollDoc mode;
    mode.start();
    weva_element_set_attribute(mode.doc,mode.at("#s"),"multiple",nullptr);
    weva_element_set_attribute(mode.doc,mode.at("#s"),"size","1");
    mode.update(0.05); CHECK(!weva_document_needs_input_tick(mode.doc));
}
