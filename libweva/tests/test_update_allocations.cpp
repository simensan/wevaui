// Diagnostic executable: measured C ABI work excludes construction and reporting.
#include "weva_c.h"
#include <cstdio>
#include <cstdlib>
#include <new>
#include <string>
#include <cstring>
#ifdef _WIN32
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <dbghelp.h>
#endif
namespace {
bool counting=false;
size_t allocations=0, bytes=0;
struct Allocation { size_t size; void* frames[12]; unsigned short count; };
Allocation samples[512]{};
bool capture=false;
void record(size_t size) {
    if(capture && allocations<512) {
        auto& sample=samples[allocations];sample.size=size;
#ifdef _WIN32
        sample.count=CaptureStackBackTrace(1,12,sample.frames,nullptr);
#endif
    }
    ++allocations;bytes+=size;
}
}
void* operator new(size_t size) {
    if (counting) record(size);
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

int main() {
    weva_config cfg{};cfg.viewport_width=1280;cfg.viewport_height=720;cfg.use_user_agent_stylesheet=1;
    const auto doc=weva_document_create(&cfg);
    std::string html="<main><div id=clock>12:00</div><button id=a>Sort</button><button id=b>Forage</button>";
    for(int i=0;i<24;++i) html+="<div class=card>Inventory item</div>";
    html+="</main>";
    const char* css="html,body{margin:0}main{width:1000px}button{display:block;width:100px;height:30px;border:1px solid #abc;background:#123}button:hover{background:#456;border-color:#def}.card{height:20px;color:#abc;background:#123}";
    if(weva_document_load_html(doc,html.data(),html.size())!=WEVA_OK ||
       weva_document_set_css(doc,css,std::strlen(css))!=WEVA_OK || weva_document_update(doc,0)!=WEVA_OK) return 2;
    double ax,ay,aw,ah,bx,by,bw,bh;
    weva_element_bounds(doc,weva_document_query(doc,"#a"),&ax,&ay,&aw,&ah);
    weva_element_bounds(doc,weva_document_query(doc,"#b"),&bx,&by,&bw,&bh);
    const auto update=[&](int i){weva_document_set_pointer(doc,i%2?ax+aw/2:bx+bw/2,i%2?ay+ah/2:by+bh/2,0);return weva_document_update(doc,0)==WEVA_OK;};
    for(int i=0;i<100;++i) if(!update(i)) return 3;
    allocations=bytes=0;counting=true;
    bool ok=true;for(int i=0;i<1000;++i) ok=update(i)&&ok;
    counting=false;ok=ok && weva_document_query(doc,"button:hover")==weva_document_query(doc,"#a");std::printf("hover: 1000 updates, %zu allocations, %zu bytes, status %d\n",allocations,bytes,ok);
    allocations=bytes=0;capture=counting=true;ok=update(0)&&ok;counting=false;
#ifdef _WIN32
    SymInitialize(GetCurrentProcess(),nullptr,TRUE);
#endif
    for(size_t i=0;i<allocations && i<512;++i){
        const auto& a=samples[i];std::printf("allocation %zu: %zu bytes\n",i,a.size);
#ifdef _WIN32
        for(unsigned j=0;j<a.count;++j){
            alignas(SYMBOL_INFO) unsigned char buffer[sizeof(SYMBOL_INFO)+1024]{};
            auto* info=reinterpret_cast<SYMBOL_INFO*>(buffer);info->SizeOfStruct=sizeof(SYMBOL_INFO);info->MaxNameLen=1023;
            DWORD64 displacement=0;
            if(SymFromAddr(GetCurrentProcess(),reinterpret_cast<DWORD64>(a.frames[j]),&displacement,info)) std::printf("  %s + %llu\n",info->Name,static_cast<unsigned long long>(displacement));
        }
#endif
    }
    ok=ok && weva_document_query(doc,"button:hover")==weva_document_query(doc,"#b");
    weva_document_destroy(doc);return ok?0:1;
}
