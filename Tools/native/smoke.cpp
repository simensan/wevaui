// A consumer of the public C API; no Unity, Godot or Agentic dependency.
#include "weva_c.h"
#include <cstdio>
#include <cstring>

int main() {
    if (weva_abi_version() != ((WEVA_ABI_VERSION_MAJOR << 16) | WEVA_ABI_VERSION_MINOR)) return 1;
    weva_config config{};
    config.viewport_width = 640;
    config.viewport_height = 480;
    config.use_user_agent_stylesheet = 1;
    auto* doc = weva_document_create(&config);
    if (!doc) return 2;
    const char* html = "<body><button>Native SDK</button></body>";
    bool ok = weva_document_load_html(doc, html, std::strlen(html)) == WEVA_OK;
    ok = ok && weva_document_update(doc, 0) == WEVA_OK;
    auto button = weva_document_focus_next(doc, 0);
    double x = 0, y = 0, width = 0, height = 0;
    ok = ok && button != WEVA_ELEMENT_NONE;
    ok = ok && weva_element_bounds(doc, button, &x, &y, &width, &height) == WEVA_OK;
    size_t count = 0;
    ok = ok && weva_document_draws(doc, &count) != nullptr && count > 0;
    ok = ok && width > 0 && height > 0;
    weva_document_destroy(doc);
    std::printf("Native SDK document/layout/draw smoke: %s\n", ok ? "passed" : "failed");
    return ok ? 0 : 3;
}
