#include "weva_c.h"
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <new>
#include <string>
namespace { bool counting=false; size_t allocations=0; }
void* operator new(size_t size) {
    if (counting) ++allocations;
    void* p = std::malloc(size ? size : 1);
    if (!p) std::abort();
    return p;
}
void* operator new[](size_t size) { return ::operator new(size); }
void* operator new(size_t size, const std::nothrow_t&) noexcept { return ::operator new(size); }
void* operator new[](size_t size, const std::nothrow_t&) noexcept { return ::operator new(size); }
void operator delete(void* p, const std::nothrow_t&) noexcept { std::free(p); }
void operator delete[](void* p, const std::nothrow_t&) noexcept { std::free(p); }
void operator delete(void* p) noexcept { std::free(p); }
void operator delete[](void* p) noexcept { std::free(p); }
void operator delete(void* p, size_t) noexcept { std::free(p); }
void operator delete[](void* p, size_t) noexcept { std::free(p); }

struct Source {
    std::string path;
    int calls=0;
    const char* value="true";
    bool missing=false;
    bool correct=true;
    static size_t read(void* user,const char* path,char* buffer,size_t capacity,int* found) {
        auto& s=*static_cast<Source*>(user);++s.calls;s.correct &= s.path==path;
        *found=!s.missing;
        if(s.missing) return 0;
        const size_t length=std::strlen(s.value);
        if(capacity>0){const size_t n=capacity-1<length?capacity-1:length;std::memcpy(buffer,s.value,n);buffer[n]=0;}
        return length;
    }
};
int main(){
    bool ok=true;
    for(int length : {15,16,63,127,128,300}) {
        Source data;data.path=std::string(length,'p');
        const std::string html="<div>{{ "+data.path+" }}</div>";
        weva_config cfg{};cfg.viewport_width=400;cfg.viewport_height=300;cfg.use_user_agent_stylesheet=1;
        const auto doc=weva_document_create(&cfg);ok &= weva_document_load_html(doc,html.data(),html.size())==WEVA_OK;
        weva_binding_source source{};source.user=&data;source.value=&Source::read;weva_document_set_binding_source(doc,&source);
        for(int i=0;i<100;++i) weva_document_refresh_bindings(doc);
        allocations=0;data.calls=0;counting=true;
        for(int i=0;i<1000;++i) ok &= weva_document_refresh_bindings(doc)==0;
        counting=false;
        std::printf("binding path %d: %zu allocations, %d reads, valid %d\n",length,allocations,data.calls,data.correct);
        ok &= data.correct && data.calls==1000 && allocations==(length<128?0:1000);
        char buffer[16];
        const auto element=weva_document_query(doc,"div");
        ok &= weva_element_text(doc,element,buffer,sizeof(buffer))==4 && std::strcmp(buffer,"true")==0;
        data.value="no";ok &= weva_document_refresh_bindings(doc)==1;
        ok &= weva_element_text(doc,element,buffer,sizeof(buffer))==2 && std::strcmp(buffer,"no")==0;
        data.missing=true;ok &= weva_document_refresh_bindings(doc)==1;
        ok &= weva_element_text(doc,element,buffer,sizeof(buffer))==0;
        data.missing=false;ok &= weva_document_refresh_bindings(doc)==1;
        ok &= weva_element_text(doc,element,buffer,sizeof(buffer))==2 && std::strcmp(buffer,"no")==0;
        weva_document_destroy(doc);
    }
    return ok?0:1;
}
