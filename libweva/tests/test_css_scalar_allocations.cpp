#include "weva/css_value.h"
#include "weva/animation.h"
#include <cstdio>
#include <cstdlib>
#include <new>
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

int main() {
    const char* text[] = {" #abc "," 12px "," 0.5 "};
    const weva::CssValueKind kinds[] = {weva::CssValueKind::Color,weva::CssValueKind::Length,weva::CssValueKind::Number};
    for(int i=0;i<100;++i) for(const char* s:text) weva::parse_css_value(s,nullptr);
    bool valid=true;counting=true;
    for(int i=0;i<1000;++i) for(int j=0;j<3;++j) {
        auto v=weva::parse_css_value(text[j],nullptr);valid=valid && v && v->kind()==kinds[j];
    }
    counting=false;
    std::printf("scalar CSS: 3000 parses, %zu allocations, values %s\n",allocations,valid?"match":"DIFFER");
    // Each returned polymorphic value owns exactly one allocation; no temporary lists.
    const bool parsing_ok = valid && allocations==3000;
    const char* from[] = {"100px", "0.25", "10%"};
    const char* to[] = {"300px", "0.75", "30%"};
    const char* expected[] = {"200px", "0.5", "20%"};
    std::string output;
    output.reserve(64);
    allocations=0;counting=true;
    for(int i=0;i<1000;++i) for(int j=0;j<3;++j) {
        valid = weva::interpolate_css(from[j],to[j],.5,&output) && output==expected[j] && valid;
    }
    counting=false;
    std::printf("scalar interpolation: 3000 samples, %zu allocations, values %s\n",allocations,valid?"match":"DIFFER");
    // At most the two parsed endpoint values per sample; no temporary lists.
    return parsing_ok && valid && allocations<=6000 ? 0:1;
}
