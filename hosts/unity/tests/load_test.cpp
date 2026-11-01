// Loads the Unity plugin the way a host would, through the dynamic loader
// and the exported symbols alone, and runs a document end to end: ABI
// version, create, HTML, CSS, update, an element's bounds, the draw list,
// destroy. No Unity involved; this is the gate for the plugin build itself.
#include "weva_unity.h"

#include <cstdio>
#include <cstring>
#include <string>
#include <chrono>
#include <thread>

#if defined(WEVA_STATIC_PLUGIN)
// The standalone static consumer links only the archives in the artifact
// directory, without the plugin build's transitive CMake target dependencies.
#elif defined(_WIN32)
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

#ifndef WEVA_STATIC_PLUGIN
template <typename F>
static F symbol(void* library, const char* name) {
    void* address = resolve(library, name);
    check(address != nullptr, name);
    return reinterpret_cast<F>(address);
}
#endif

int main(int argc, char** argv) {
#ifdef WEVA_STATIC_PLUGIN
    (void)argc;
    (void)argv;
#define WEVA_SYMBOL(name) &name
    {
#else
    if (argc < 2) {
        std::fprintf(stderr, "usage: weva_core_load_test <path to weva_core plugin>\n");
        return 2;
    }
    for (int iteration = 0; iteration < 2; ++iteration) {
    void* library = open_library(argv[1]);
    check(library != nullptr, "plugin loads through the dynamic loader");
    if (!library) return 1;
#define WEVA_SYMBOL(name) symbol<decltype(&name)>(library, #name)
#endif

    auto abi_version = WEVA_SYMBOL(weva_abi_version);
    auto create = WEVA_SYMBOL(weva_document_create);
    auto load_html = WEVA_SYMBOL(weva_document_load_html);
    auto set_css = WEVA_SYMBOL(weva_document_set_css);
    auto set_reader = WEVA_SYMBOL(weva_document_set_asset_reader);
    auto animating = WEVA_SYMBOL(weva_document_is_animating);
    auto update = WEVA_SYMBOL(weva_document_update);
    auto focus_next = WEVA_SYMBOL(weva_document_focus_next);
    auto bounds = WEVA_SYMBOL(weva_element_bounds);
    auto tag_name = WEVA_SYMBOL(weva_element_tag_name);
    auto draws = WEVA_SYMBOL(weva_document_draws);
    auto destroy = WEVA_SYMBOL(weva_document_destroy);
    auto size_of = WEVA_SYMBOL(weva_unity_sizeof);
#undef WEVA_SYMBOL
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
    // Real async image work must retire before native code is unloaded.
    // The old process-global worker owner pinned Windows DLLs forever.
    static const uint8_t png[] = {
    137, 80, 78, 71, 13, 10, 26, 10, 0, 0, 0, 13, 73, 72, 68, 82,
    0, 0, 0, 2, 0, 0, 0, 2, 8, 6, 0, 0, 0, 114, 182, 13,
    36, 0, 0, 0, 24, 73, 68, 65, 84, 120, 156, 99, 224, 18, 145, 211,
    56, 49, 45, 229, 63, 3, 8, 252, 255, 255, 191, 1, 0, 46, 215, 6,
    163, 211, 42, 222, 53, 0, 0, 0, 0, 73, 69, 78, 68, 174, 66, 96,
    130,
};
    int reads = 0;
    const auto reader = [](void* user, const char*, uint8_t* buffer, size_t capacity) -> size_t {
        ++*static_cast<int*>(user);
        if (buffer && capacity >= sizeof(png)) std::memcpy(buffer, png, sizeof(png));
        return sizeof(png);
    };
    check(set_reader(doc, reader, &reads) == WEVA_OK, "asset reader installed");
    const std::string async_html = "<img decoding='async' src='icon.png' style='display:block;width:64px;height:64px'>";
    check(load_html(doc, async_html.data(), async_html.size()) == WEVA_OK, "async image mounts");
    const auto deadline = std::chrono::steady_clock::now() + std::chrono::seconds(5);
    do {
        check(update(doc, 0.016) == WEVA_OK, "async image updates");
        if (reads >= 2 && !animating(doc)) break;
        std::this_thread::sleep_for(std::chrono::milliseconds(1));
    } while (std::chrono::steady_clock::now() < deadline);
    check(reads >= 2 && !animating(doc), "async read/decode settles");
    destroy(doc);
#ifndef WEVA_STATIC_PLUGIN
#if defined(_WIN32)
    check(FreeLibrary(static_cast<HMODULE>(library)) != 0, "plugin release succeeds");
    check(GetModuleHandleA(argv[1]) == nullptr, "async workers do not pin the unloaded module");
#else
    check(dlclose(library) == 0, "plugin closes after async decode");
#endif
#endif
    }
    std::printf("weva_core plugin: %s\n", failures ? "FAILED" : "ok");
    return failures ? 1 : 0;
}
