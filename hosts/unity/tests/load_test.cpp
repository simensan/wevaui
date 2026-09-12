// Loads the Unity plugin the way a host would, through the dynamic loader
// and the exported symbols alone, and runs a document end to end: ABI
// version, create, HTML, CSS, update, an element's bounds, the draw list,
// destroy. No Unity involved; this is the gate for the plugin build itself.
#include "weva_unity.h"

#include <cstdio>
#include <cstring>
#include <string>

#ifdef _WIN32
#include <windows.h>
static void* open_library(const char* path) { return LoadLibraryA(path); }
static void* resolve(void* library, const char* name) {
    return reinterpret_cast<void*>(GetProcAddress(static_cast<HMODULE>(library), name));
}
#else
#include <dlfcn.h>
static void* open_library(const char* path) { return dlopen(path, RTLD_NOW | RTLD_LOCAL); }
static void* resolve(void* library, const char* name) { return dlsym(library, name); }
#endif

static int failures = 0;
static void check(bool ok, const char* message) {
    if (!ok) {
        ++failures;
        std::fprintf(stderr, "FAIL plugin: %s\n", message);
    }
}

template <typename F>
static F symbol(void* library, const char* name) {
    void* address = resolve(library, name);
    check(address != nullptr, name);
    return reinterpret_cast<F>(address);
}

int main(int argc, char** argv) {
    if (argc < 2) {
        std::fprintf(stderr, "usage: weva_core_load_test <path to weva_core plugin>\n");
        return 2;
    }
    void* library = open_library(argv[1]);
    check(library != nullptr, "plugin loads through the dynamic loader");
    if (!library) return 1;

    auto abi_version = symbol<decltype(&weva_abi_version)>(library, "weva_abi_version");
    auto create = symbol<decltype(&weva_document_create)>(library, "weva_document_create");
    auto load_html = symbol<decltype(&weva_document_load_html)>(library, "weva_document_load_html");
    auto set_css = symbol<decltype(&weva_document_set_css)>(library, "weva_document_set_css");
    auto update = symbol<decltype(&weva_document_update)>(library, "weva_document_update");
    auto focus_next = symbol<decltype(&weva_document_focus_next)>(library, "weva_document_focus_next");
    auto bounds = symbol<decltype(&weva_element_bounds)>(library, "weva_element_bounds");
    auto tag_name = symbol<decltype(&weva_element_tag_name)>(library, "weva_element_tag_name");
    auto draws = symbol<decltype(&weva_document_draws)>(library, "weva_document_draws");
    auto destroy = symbol<decltype(&weva_document_destroy)>(library, "weva_document_destroy");
    auto size_of = symbol<decltype(&weva_unity_sizeof)>(library, "weva_unity_sizeof");
    if (failures) return 1;

    const uint32_t version = abi_version();
    check((version >> 16) == WEVA_ABI_VERSION_MAJOR && (version & 0xFFFF) == WEVA_ABI_VERSION_MINOR,
          "plugin reports the ABI version its header declares");
    std::printf("weva_core abi %u.%u\n", version >> 16, version & 0xFFFF);
    check(size_of("weva_draw") == sizeof(weva_draw) && size_of("weva_event") == sizeof(weva_event) &&
              size_of("weva_font_backend") == sizeof(weva_font_backend),
          "the layout probe reports the header's struct sizes");
    check(size_of("weva_nothing") == 0 && size_of(nullptr) == 0, "the layout probe rejects unknown names");

    weva_config config{};
    config.viewport_width = 640;
    config.viewport_height = 480;
    config.use_user_agent_stylesheet = 1;
    weva_document_t doc = create(&config);
    check(doc != nullptr, "document created through the plugin");
    if (!doc) return 1;
    const std::string html = "<body><button id=go>Go</button><div id=box>box</div></body>";
    const std::string css = "#box{width:120px;height:40px;background:#3a5f8a;margin:10px}button{margin:10px}";
    check(load_html(doc, html.data(), html.size()) == WEVA_OK, "HTML loads");
    check(set_css(doc, css.data(), css.size()) == WEVA_OK, "CSS applies");
    check(update(doc, 0) == WEVA_OK, "document updates");
    const weva_element_t button = focus_next(doc, 0);
    check(button != WEVA_ELEMENT_NONE, "focus finds the button");
    char tag[16] = {};
    check(tag_name(doc, button, tag, sizeof(tag)) == 6 && std::strcmp(tag, "button") == 0, "tag name round-trips");
    double x = 0, y = 0, w = 0, h = 0;
    check(bounds(doc, button, &x, &y, &w, &h) == WEVA_OK && w > 0 && h > 0, "button has laid-out bounds");
    std::printf("button at %.1f,%.1f size %.1fx%.1f\n", x, y, w, h);
    size_t count = 0;
    const auto* list = draws(doc, &count);
    check(list != nullptr && count > 0, "the document produces draws");
    std::printf("%zu draws\n", count);
    destroy(doc);
    std::printf("weva_core plugin: %s\n", failures ? "FAILED" : "ok");
    return failures ? 1 : 0;
}
