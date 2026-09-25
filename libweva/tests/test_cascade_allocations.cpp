// A separate executable keeps the allocation counter out of the general
// test runner. Count only cascade work, excluding the result assertions.
#include "weva/cascade.h"
#include "weva/dom.h"
#include "weva/html.h"
#include <array>
#include <cstdio>
#include <cstdlib>
#include <new>
#include <string>

namespace {
bool counting = false;
size_t allocations = 0;
}

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
    weva::SymbolTable symbols;
    weva::HtmlParseError error;
    weva::ParseOptions options; options.strict = false;
    auto doc = weva::parse_html("<div id=field>Text</div>", &symbols, options, &error);
    weva::Stylesheet sheet; weva::CssParseError css_error;
    if (!doc || !weva::parse_stylesheet("div{color:red;background-color:blue;width:120px}",false,&sheet,&css_error)) return 2;
    weva::CascadeEngine engine;
    engine.add_stylesheet(&sheet,weva::DeclarationOrigin::Author);
    weva::NullStateProvider state;
    weva::ComputedStyle output;
    auto* element = doc->get_element_by_id("field");
    for (int i=0;i<100;++i) engine.compute(*element,state,nullptr,&output);
    // Positive control verifies that the linked implementation uses our counter.
    counting = true;
    output.set("--allocation-positive-control-long-property", "value");
    counting = false;
    bool valid = allocations > 0;
    engine.compute(*element,state,nullptr,&output);
    allocations = 0;
    counting = true;
    for (int i=0;i<1000;++i) {
        engine.compute(*element,state,nullptr,&output);
    }
    counting = false;
    valid &= output.get("color") == "red";
    valid &= output.get("background-color") == "blue";
    valid &= output.get("width") == "120px";
    std::printf("cached cascade: 1000 computations, %zu allocations, values %s\n",allocations,valid?"match":"DIFFER");
    return valid && allocations == 0 ? 0 : 1;
}
