#pragma once
#include <cstdint>
#include <string_view>
#include <vector>

// Decoding a bitmap to straight RGBA8, in the CORE rather than in a host.
//
// The engine parsed `url(...)` into a background layer and then never looked
// at it again: `layer.url` was written in one place and read in none, so an
// <img>, a background-image and a border-image-source all degraded to a flat
// fill. This is the missing half.
//
// It lives here, and not in each host, for the reason the glyph atlas does:
// the core ships pixels through weva_document_textures and every backend
// uploads the same bytes. One decoder means the software renderer and Godot
// cannot disagree about an image, which is what keeps the render gate exact.
// A host-side decoder would have put two different libraries on the two sides
// of that comparison.
//
// PNG only, and only the subset UI art is actually stored in: 8 bits per
// channel, no interlacing. Anything else is refused rather than guessed at,
// and a refused image degrades to the flat fill it already did.

namespace weva {

struct DecodedImage {
    int width = 0;
    int height = 0;
    // Straight (not premultiplied) RGBA, row-major from the top left.
    std::vector<uint8_t> rgba;

    bool valid() const { return width > 0 && height > 0 && !rgba.empty(); }
};

// RFC 1951 DEFLATE. Exposed on its own because it is the half worth testing
// on its own -- a PNG that decodes wrongly is nearly always inflate.
// `limit` bounds the inflated size: decoding stops, successfully, once the
// output holds that many bytes -- as libpng ignores data past the last row --
// so a few kilobytes of repeated matches cannot expand into gigabytes.
bool inflate_deflate(const uint8_t* data, size_t size, std::vector<uint8_t>* out,
                     size_t limit = SIZE_MAX);

// RFC 1950 zlib wrapper (a two-byte header, then DEFLATE, then a checksum).
bool inflate_zlib(const uint8_t* data, size_t size, std::vector<uint8_t>* out,
                  size_t limit = SIZE_MAX);

// A PNG's bytes to RGBA8. Returns an invalid image on anything unsupported.
DecodedImage decode_png(const uint8_t* data, size_t size);

// Dispatches on the content, not on the file extension: a .png that is really
// something else should fail as what it is.
DecodedImage decode_image(const uint8_t* data, size_t size);

} // namespace weva
