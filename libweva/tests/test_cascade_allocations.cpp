// A separate executable keeps the allocation counter out of the general
// test runner. Count only cascade work, excluding the result assertions.
#include "weva/cascade.h"
#include "weva/dom.h"
#include "weva/html.h"
#include "weva_c.h"
#include <array>
#include <cstdio>
#include <cstdlib>
#include <new>
#include <string>
#include <vector>

namespace {
bool counting = false;
size_t allocations = 0;
size_t allocated_bytes = 0;
}

void* operator new(size_t size) {
    if (counting) { ++allocations; allocated_bytes += size; }
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
    valid &= allocations == 0;

    // Cold styles for a hidden panel need raw values, but no parsed syntax
    // until layout/paint asks for it. Keep 1000 sparse styles below 4 MiB of
    // allocation traffic instead of reserving a parse-cache entry for every
    // registered property on every style.
    allocations = 0;
    allocated_bytes = 0;
    counting = true;
    std::vector<weva::ComputedStyle> hidden(1000);
    for (auto& style : hidden) {
        style.set("display", "none");
        style.set("color", "red");
        style.set("width", "120px");
    }
    counting = false;
    const auto cold_bytes = allocated_bytes;
    for (auto& style : hidden) {
        valid &= style.get("color") == "red";
        valid &= style.parsed("width") && style.parsed("width")->raw == "120px";
    }
    std::printf("cold sparse styles: 1000 styles, %zu bytes, values %s\n", cold_bytes, valid?"match":"DIFFER");
    valid &= cold_bytes < 4 * 1024 * 1024;

    // Replacing markup with no document styles must not pay again for the
    // already-installed host sheet. Compare identical DOM replacements with
    // and without a large unrelated sheet, excluding initial CSS installation.
    weva_config config{};
    config.viewport_width = 320;
    config.viewport_height = 200;
    auto plain = weva_document_create(&config);
    auto styled = weva_document_create(&config);
    std::string unrelated;
    for (int i = 0; i < 400; ++i)
        unrelated += ".unrelated-selector-" + std::to_string(i) + "{width:17px;color:red}";
    valid &= weva_document_set_css(styled, unrelated.data(), unrelated.size()) == WEVA_OK;
    constexpr std::string_view markup = "<div id=field>Replacement</div>";
    const auto reload_allocations = [&](weva_document_t document) {
        allocations = 0;
        counting = true;
        const auto status = weva_document_load_html(document, markup.data(), markup.size());
        counting = false;
        valid &= status == WEVA_OK;
        return allocations;
    };
    const auto plain_load = reload_allocations(plain);
    const auto styled_load = reload_allocations(styled);
    std::printf("markup load: plain %zu allocations, installed 400-rule sheet %zu\n", plain_load, styled_load);
    valid &= styled_load <= plain_load + 128;
    weva_document_destroy(plain);
    weva_document_destroy(styled);
    return valid ? 0 : 1;
}
