#pragma once
#include "check.h"
#include "weva_c.h"
#include <cstring>
#include <string>
#include <vector>

namespace {
struct AutoScrollDoc {
    weva_document_t doc;
    std::string html;
    mutable int scroll_events=0;
    explicit AutoScrollDoc(bool multiple = true) {
        weva_config cfg{};
        cfg.viewport_width=640; cfg.viewport_height=480; cfg.use_user_agent_stylesheet=1;
        doc=weva_document_create(&cfg);
        const char* css="html,body{margin:0}#outer{position:absolute;left:100px;top:60px;width:240px;height:220px;overflow:auto}"
            "#lead{height:40px}#tail{height:240px}select{display:block;width:220px;height:110px;padding:0;border:0}"
            "option{height:24px;padding:0}#clock{position:absolute;top:400px;width:20px;height:20px;background:red;"
            "animation:travel 2s linear infinite}@keyframes travel{from{left:0px}to{left:200px}}";
        weva_document_add_css(doc,css,std::strlen(css));
        html="<form id=f><div id=outer><div id=lead></div><select id=s size=4";
        html+=multiple ? " multiple>" : ">";
        for(int i=0;i<20;++i) {
            html+="<option id=o"+std::to_string(i)+" value="+std::to_string(i);
            if(i==5 || i==19) html+=" disabled";
            html+=">Row "+std::to_string(i)+"</option>";
        }
        html+="</select><div id=tail></div></div></form><div id=clock></div>";
        weva_document_load_html(doc,html.data(),html.size()); update(); events();
    }
    ~AutoScrollDoc(){weva_document_destroy(doc);}
    weva_element_t at(const char* selector) const {return weva_document_query(doc,selector);}
    void update(double input=0,double animation=0) {
        CHECK(weva_document_update_with_input_time(doc,animation,input)==WEVA_OK);
    }
    struct Bounds {double x=0,y=0,w=0,h=0;};
    Bounds bounds(const char* selector) const {
        Bounds b;
        CHECK(weva_element_bounds(doc,at(selector),&b.x,&b.y,&b.w,&b.h)==WEVA_OK);
        return b;
    }
    void pointer(double x,double y,uint32_t buttons=1,uint32_t mods=0) {
        weva_document_set_pointer_modifiers(doc,x,y,buttons,mods); update();
    }
    void row(int index,uint32_t buttons=1,uint32_t mods=0) {
        auto b=bounds(("#o"+std::to_string(index)).c_str());
        pointer(b.x+b.w/2,b.y+b.h/2,buttons,mods);
    }
    void below(uint32_t buttons=1) {auto b=bounds("#s");pointer(b.x+b.w/2,b.y+b.h+30,buttons);}
    void start(uint32_t mods=0) {row(1,1,mods); row(2,1,mods); below();}
    double scroll(double* maximum=nullptr) const {
        double y=0;
        CHECK(weva_element_scroll(doc,at("#s"),nullptr,&y,nullptr,maximum)==WEVA_OK);
        return y;
    }
    std::vector<int> selected() const {
        std::vector<int> out;
        for(int i=0;i<20;++i)
            if(at(("#o"+std::to_string(i)+":checked").c_str())!=WEVA_ELEMENT_NONE) out.push_back(i);
        return out;
    }
    std::vector<int> events() const {
        std::vector<int> out; weva_event event{};
        while(weva_document_poll_event(doc,&event)) {
            if(event.kind==WEVA_EVENT_VALUE_CHANGED || event.kind==WEVA_EVENT_CHANGE) out.push_back(event.kind);
            if(event.kind==WEVA_EVENT_SCROLL) ++scroll_events;
        }
        return out;
    }
};
}
