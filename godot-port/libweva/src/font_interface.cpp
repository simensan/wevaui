#include "weva/font_interface.h"

#include <algorithm>
#include <cmath>

namespace weva {

namespace {

#include "generated/stub_font.inc"

constexpr int kCellW = 5;
constexpr int kCellH = 7;
constexpr uint32_t kFirst = 0x20;
constexpr uint32_t kLast = 0x7E;

// The em box the 5x7 cell is scaled into. 0.5em of advance per glyph matches
// MonoFontMetrics, so measurement and rendering agree — a mismatch would show
// as text drifting away from the line boxes laid out for it.
constexpr double kAdvanceEm = 0.5;
constexpr double kAscentEm = 0.8;
constexpr double kDescentEm = 0.4;
constexpr double kLineHeightEm = 1.2;

size_t next_code_point(std::string_view s, size_t i, uint32_t* cp) {
    const unsigned char c = static_cast<unsigned char>(s[i]);
    size_t len = 1;
    uint32_t v = c;
    if ((c & 0xE0) == 0xC0) { len = 2; v = c & 0x1Fu; }
    else if ((c & 0xF0) == 0xE0) { len = 3; v = c & 0x0Fu; }
    else if ((c & 0xF8) == 0xF0) { len = 4; v = c & 0x07u; }
    if (i + len > s.size()) { *cp = c; return 1; }
    for (size_t k = 1; k < len; ++k) {
        const unsigned char cc = static_cast<unsigned char>(s[i + k]);
        if ((cc & 0xC0) != 0x80) { *cp = c; return 1; }
        v = (v << 6) | (cc & 0x3Fu);
    }
    *cp = v;
    return len;
}

// True when a glyph's cell is entirely blank — a space, or the missing glyph.
// Such a glyph has an advance but NO bitmap, exactly as in a real face, so the
// atlas does not pack an empty rectangle for every space in the document.
bool glyph_is_blank(uint32_t glyph) {
    if (glyph == 0 || glyph > 95) return true;
    const uint8_t* rows = kStubFontRows[glyph - 1];
    for (int i = 0; i < kCellH; ++i) {
        if (rows[i] != 0) return false;
    }
    return true;
}

} // namespace

FaceHandle StubFont::load_face(const std::vector<uint8_t>&, int) {
    // There is one built-in face and no file to read, so any request gets it.
    // A real backend parses the bytes; this exists so the pipeline runs before
    // that backend does.
    return builtin();
}

bool StubFont::face_metrics(FaceHandle face, double px, FaceMetrics* out) {
    if (!face || !out) return false;
    out->ascent = px * kAscentEm;
    out->descent = px * kDescentEm;
    out->line_gap = px * (kLineHeightEm - kAscentEm - kDescentEm);
    out->units_per_em = px;
    return true;
}

bool StubFont::glyph_index(FaceHandle face, uint32_t codepoint, uint32_t* out) {
    if (!face || !out) return false;
    // Glyph 0 is the standard "missing glyph" slot, so an unmapped code point
    // reports a hit with index 0 rather than failing — the caller then draws
    // nothing for it instead of dropping the whole run.
    *out = (codepoint < kFirst || codepoint > kLast) ? 0 : codepoint - kFirst + 1;
    return true;
}

bool StubFont::glyph_metrics(FaceHandle face, uint32_t glyph, double px, GlyphMetrics* out) {
    if (!face || !out) return false;
    out->advance = px * kAdvanceEm;
    out->bearing_x = 0;
    if (glyph_is_blank(glyph)) {
        out->bearing_y = 0;
        out->width = 0;
        out->height = 0;
        return true;
    }
    const int h = std::max(1, static_cast<int>(std::lround(px * kAscentEm)));
    out->width = std::max(1, static_cast<int>(std::lround(h * (double(kCellW) / kCellH))));
    out->height = h;
    // The bearing is the ROUNDED cell height, not the exact ascent. Using the
    // ascent would leave the quad's bottom edge a fraction of a pixel below the
    // baseline, since the bitmap is a whole number of pixels tall — a real
    // face's bearing is per-glyph and has no reason to match the face ascent
    // either.
    out->bearing_y = h;
    return true;
}

bool StubFont::rasterize(FaceHandle face, uint32_t glyph, double px, RenderMode mode,
                         Bitmap* out) {
    // Alpha8 only: an SDF needs a distance transform, which belongs with the
    // real backend.
    if (!face || !out || mode != RenderMode::Alpha8) return false;
    GlyphMetrics gm;
    if (!glyph_metrics(face, glyph, px, &gm)) return false;
    out->width = gm.width;
    out->height = gm.height;
    out->data.assign(static_cast<size_t>(gm.width) * gm.height, 0);
    if (gm.width <= 0 || gm.height <= 0) return true;

    const uint8_t* rows = kStubFontRows[glyph - 1];
    // Nearest-neighbour upscale of the 5x7 cell. Blocky at large sizes, and
    // deliberately so: a deterministic result is what makes pixel assertions
    // possible on every machine.
    for (int y = 0; y < out->height; ++y) {
        const int sy = std::min(kCellH - 1, y * kCellH / out->height);
        for (int x = 0; x < out->width; ++x) {
            const int sx = std::min(kCellW - 1, x * kCellW / out->width);
            const bool on = (rows[sy] >> (kCellW - 1 - sx)) & 1;
            out->data[static_cast<size_t>(y) * out->width + x] = on ? 255 : 0;
        }
    }
    return true;
}

void StubFont::shape(FaceHandle face, std::string_view utf8, double px,
                     std::vector<ShapedGlyph>* out) {
    if (!out) return;
    out->clear();
    if (!face) return;
    // One glyph per code point, in order: no ligatures, no reordering, no
    // kerning. `cluster` still carries the byte offset, so a caller mapping
    // back to source text works the same as it will with a real shaper.
    for (size_t i = 0; i < utf8.size();) {
        uint32_t cp = 0;
        const size_t len = next_code_point(utf8, i, &cp);
        uint32_t glyph = 0;
        glyph_index(face, cp, &glyph);
        ShapedGlyph g;
        g.glyph = glyph;
        g.x_advance = px * kAdvanceEm;
        g.cluster = static_cast<uint32_t>(i);
        out->push_back(g);
        i += len;
    }
}

} // namespace weva
