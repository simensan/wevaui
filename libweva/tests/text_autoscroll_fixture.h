#pragma once
#include "check.h"
#include "weva_c.h"
#include <cstring>
#include <string>
#include <utility>
#include <vector>

namespace {
struct TextScrollDoc {
    weva_document_t doc;
    std::string html,value;
    mutable double scroll_event_x=0,scroll_event_y=0;
    mutable int scroll_events=0;
    explicit TextScrollDoc(const char* kind="text",bool wrap=false) {
        const bool area=std::strcmp(kind,"textarea")==0;
        weva_config cfg{};cfg.viewport_width=640;cfg.viewport_height=480;cfg.use_user_agent_stylesheet=1;
        doc=weva_document_create(&cfg);
        const std::string css=std::string("html,body{margin:0}#outer{position:absolute;left:100px;top:70px;width:250px;height:200px;overflow:auto}#lead{height:30px}#tail{height:250px}")+
            "input,textarea{display:block;width:160px;padding:0;border:0;font-size:16px;line-height:24px}input{height:32px}textarea{height:96px;overflow:auto;white-space:"+(wrap?"pre-wrap":"pre")+"}"
            "#clock{position:absolute;top:400px;width:20px;height:20px;background:red;animation:travel 2s linear infinite}@keyframes travel{from{left:0px}to{left:200px}}";
        weva_document_add_css(doc,css.data(),css.size());
        html="<form id=form><div id=outer><div id=lead></div>";
        html+=area?"<textarea id=f></textarea>":"<input id=f type="+std::string(kind)+">";
        html+="<div id=tail></div></div></form><button id=other>Other</button><div id=clock></div>";
        weva_document_load_html(doc,html.data(),html.size());
        if(area) {
            for(int i=0;i<20;++i) {
                if(i) value+='\n';
                value+=std::to_string(i)+" ";
                for(int j=0;j<12;++j) value+=u8"á😀b ";
            }
        } else for(int i=0;i<100;++i) value+=u8"á😀b";
        weva_element_set_value(doc,at("#f"),value.c_str());update();
        weva_document_set_focus(doc,at("#f"));weva_element_set_selection(doc,at("#f"),0,0);update();events();
    }
    ~TextScrollDoc(){weva_document_destroy(doc);}
    weva_element_t at(const char* selector) const{return weva_document_query(doc,selector);}
    struct Bounds{double x=0,y=0,w=0,h=0;};
    Bounds bounds(const char* selector="#f") const{
        Bounds b;CHECK(weva_element_bounds(doc,at(selector),&b.x,&b.y,&b.w,&b.h)==WEVA_OK);return b;
    }
    void update(double input=0,double animation=0){CHECK(weva_document_update_with_input_time(doc,animation,input)==WEVA_OK);}
    void pointer(double x,double y,uint32_t buttons=1){weva_document_set_pointer(doc,x,y,buttons);update();}
    void start(int direction=0){
        const auto b=bounds();pointer(b.x+20,b.y+16);pointer(b.x+40,b.y+16);
        pointer(direction==1?b.x+20:b.x+b.w+40,direction==0?b.y+16:b.y+b.h+40);
    }
    std::pair<int,int> selection() const{
        int a=-1,b=-1;CHECK(weva_element_selection(doc,at("#f"),&a,&b)==WEVA_OK);return {a,b};
    }
    std::pair<double,double> scroll() const{
        double x=0,y=0;CHECK(weva_element_scroll(doc,at("#f"),&x,&y,nullptr,nullptr)==WEVA_OK);return {x,y};
    }
    std::vector<int> events() const{
        std::vector<int> out;weva_event event{};
        while(weva_document_poll_event(doc,&event)){
            if(event.kind==WEVA_EVENT_VALUE_CHANGED||event.kind==WEVA_EVENT_CHANGE)out.push_back(event.kind);
            if(event.kind==WEVA_EVENT_SCROLL){++scroll_events;scroll_event_x=event.x;scroll_event_y=event.y;}
        }
        return out;
    }
};
}
