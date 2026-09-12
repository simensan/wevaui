// A separate executable keeps the allocation counter out of the general
// test runner. Count only binding work, excluding the result assertions.
#include "weva/binding.h"
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

namespace {
struct Resolver : weva::BindingResolver {
    bool enabled = true;
    bool resolve(std::string_view, std::string* out) const override {
        *out = enabled ? "true" : "false";
        return true;
    }
};
}
int main() {
    weva::SymbolTable symbols;
    weva::HtmlParseError error;
    weva::ParseOptions options; options.strict = false;
    auto doc = weva::parse_html("<div id=field class='on long-static-class-preserved-by-binding' data-class-on=Enabled></div>", &symbols, options, &error);
    if (!doc) return 2;
    Resolver resolver;
    weva::BindingTemplates templates;
    weva::BindingRepeats repeats;
    auto* element = doc->get_element_by_id("field");
    for (int i=0;i<100;++i) weva::apply_bindings(*doc,resolver,&templates,&repeats);
    resolver.enabled = false;
    counting = true;
    const int changed = weva::apply_bindings(*doc,resolver,&templates,&repeats);
    counting = false;
    bool valid = allocations > 0 && changed == 1 && !weva::has_class_token(element->class_name(), "on");
    resolver.enabled = true;
    valid &= weva::apply_bindings(*doc,resolver,&templates,&repeats) == 1;
    allocations = 0;
    int changes = 0;
    counting = true;
    for (int i=0;i<1000;++i) changes += weva::apply_bindings(*doc,resolver,&templates,&repeats);
    counting = false;
    valid &= changes == 0 && weva::has_class_token(element->class_name(), "on") &&
        weva::has_class_token(element->class_name(), "long-static-class-preserved-by-binding");
    std::printf("unchanged class bindings: 1000 refreshes, %zu allocations, values %s\n",allocations,valid?"match":"DIFFER");
    valid &= allocations == 0;

    auto selection = weva::parse_html("<div id=selected class='inventory-item-selected authored' data-class-inventory-item-selected='Player.Inventory.Selected'></div>", &symbols, options, &error);
    if (!selection) return 2;
    weva::BindingTemplates selection_templates;
    for (int i=0;i<100;++i) weva::apply_bindings(*selection,resolver,&selection_templates);
    allocations = 0;
    changes = 0;
    counting = true;
    for (int i=0;i<1000;++i) changes += weva::apply_bindings(*selection,resolver,&selection_templates);
    counting = false;
    auto* selected = selection->get_element_by_id("selected");
    bool selection_valid = changes == 0 && selected->class_name() == "inventory-item-selected authored";
    std::printf("unchanged long class binding: 1000 refreshes, %zu allocations, values %s\n",allocations,selection_valid?"match":"DIFFER");
    valid &= selection_valid && allocations == 0;
    resolver.enabled = false;
    valid &= weva::apply_bindings(*selection,resolver,&selection_templates) == 1 && selected->class_name() == "authored";
    resolver.enabled = true;
    valid &= weva::apply_bindings(*selection,resolver,&selection_templates) == 1 &&
        weva::has_class_token(selected->class_name(), "inventory-item-selected");

    struct TextResolver : weva::BindingResolver {
        mutable int reads = 0;
        bool updated = false;
        bool resolve(std::string_view path, std::string* out) const override {
            ++reads;
            *out = path == "Count" ? (updated ? "99" : "42") : "7";
            return true;
        }
    } text_resolver;
    auto hud = weva::parse_html("<p id=h>Travel to the northern supply cache: {{ Count }} supplies, {{ Days }} days remaining.</p><div id=d data-inventory-caption='Northern supply cache contains {{ Count }} supplies ready for collection.' data-inventory-description='Static authored description'></div>", &symbols, options, &error);
    if (!hud) return 2;
    weva::BindingTemplates hud_templates;
    weva::BindingRepeats hud_repeats;
    for (int i=0;i<100;++i) weva::apply_bindings(*hud,text_resolver,&hud_templates,&hud_repeats);
    allocations = 0;
    text_resolver.reads = 0;
    changes = 0;
    counting = true;
    for (int i=0;i<1000;++i) changes += weva::apply_bindings(*hud,text_resolver,&hud_templates,&hud_repeats);
    counting = false;
    const bool hud_valid = changes == 0 && text_resolver.reads == 3000 &&
        hud->get_element_by_id("d")->get_attribute("data-inventory-caption") == "Northern supply cache contains 42 supplies ready for collection." &&
        hud->get_element_by_id("d")->get_attribute("data-inventory-description") == "Static authored description";
    std::printf("unchanged HUD text/attributes: 1000 refreshes, %zu allocations, %d reads, values %s\n",allocations,text_resolver.reads,hud_valid?"match":"DIFFER");
    valid &= hud_valid && allocations == 0;
    text_resolver.updated = true;
    valid &= weva::apply_bindings(*hud,text_resolver,&hud_templates,&hud_repeats) == 2 &&
        hud->get_element_by_id("d")->get_attribute("data-inventory-caption") ==
            "Northern supply cache contains 99 supplies ready for collection.";
    return valid ? 0 : 1;
}
