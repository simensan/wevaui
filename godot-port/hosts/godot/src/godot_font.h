#pragma once

#include "weva_c.h"

#include <godot_cpp/variant/rid.hpp>

#include <cstdint>
#include <map>
#include <vector>

// libweva's font backend, implemented over Godot's TextServer.
//
// The core ships a 5x7 stub face so it can lay out and rasterise text with no
// host at all, which is what keeps it testable. That stub is not a font, and
// this is what replaces it: real faces, real metrics, and real shaping through
// the same HarfBuzz TextServer drives for every other control in the engine.
//
// It fills the C function-pointer table rather than subclassing anything: the
// core must not see a Godot type, and a `RID` reaching across the seam would
// end that.

namespace weva_godot {

class GodotFontBackend {
public:
    // Fills a table whose `user_data` is this object. The table outlives the
    // call; the caller keeps it alive for as long as the document.
    void fill(weva_font_backend* out);

    // Adopts a face the engine already has — the theme's fallback font, say —
    // without going through a font file. Returns the handle to hand to
    // weva_document_set_font_backend.
    uint64_t adopt(const godot::RID& font);

private:
    // Every entry point is static so its address fits a C function pointer;
    // each recovers the instance from user_data.
    static uint64_t load_face(void* self, const uint8_t* data, size_t length, int32_t index);
    static int32_t face_metrics(void* self, uint64_t face, double px, double* ascent,
                                double* descent, double* line_gap);
    static int32_t glyph_index(void* self, uint64_t face, uint32_t codepoint, uint32_t* out);
    static int32_t glyph_metrics(void* self, uint64_t face, uint32_t glyph, double px,
                                 double* advance, double* bearing_x, double* bearing_y,
                                 int32_t* width, int32_t* height);
    static int32_t rasterize(void* self, uint64_t face, uint32_t glyph, double px,
                             weva_glyph_bitmap* out);
    static size_t shape(void* self, uint64_t face, const char* utf8, size_t length, double px,
                        uint32_t* glyphs, double* advances, uint32_t* clusters, size_t capacity);

    godot::RID resolve(uint64_t face) const;

    std::map<uint64_t, godot::RID> faces_;
    uint64_t next_face_ = 1;
    // The core copies the bitmap before `rasterize` returns, so one reusable
    // buffer is enough and costs no per-glyph allocation.
    std::vector<uint8_t> scratch_;
};

} // namespace weva_godot
