// The Unity plugin adds almost nothing of its own. Every document entry
// point comes from libweva's C ABI (weva_c.h), pulled from the static core by
// the generated export list; what is declared in weva_unity.h is the plugin's
// own surface, kept to what a managed host cannot find out by itself.
#include "weva_unity.h"

#include <cstring>

namespace {

struct struct_size {
    const char* name;
    size_t size;
};

#define WEVA_SIZE(type) {#type, sizeof(type)}
const struct_size sizes[] = {
    WEVA_SIZE(weva_config),        WEVA_SIZE(weva_vertex),          WEVA_SIZE(weva_rounded_rect),
    WEVA_SIZE(weva_backdrop_effect), WEVA_SIZE(weva_draw),          WEVA_SIZE(weva_texture),
    WEVA_SIZE(weva_render_backend), WEVA_SIZE(weva_glyph_bitmap),   WEVA_SIZE(weva_font_backend),
    WEVA_SIZE(weva_shaped_glyph),  WEVA_SIZE(weva_event),           WEVA_SIZE(weva_binding_source),
};
#undef WEVA_SIZE

}  // namespace

extern "C" size_t weva_unity_sizeof(const char* struct_name) {
    if (!struct_name) return 0;
    for (const auto& entry : sizes) {
        if (std::strcmp(entry.name, struct_name) == 0) return entry.size;
    }
    return 0;
}
