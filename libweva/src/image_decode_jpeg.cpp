#include "weva/image_decode.h"

#include <climits>
#include <memory>

// The decoder is private to this translation unit. All hosts consume the
// resulting core-owned RGBA bytes, including deferred ImageStore workers.
#define STB_IMAGE_STATIC
#define STB_IMAGE_IMPLEMENTATION
#define STBI_ONLY_JPEG
#define STBI_NO_STDIO
#define STBI_NO_LINEAR
#define STBI_NO_HDR
#include <stb_image.h>

namespace weva {

DecodedImage decode_jpeg(const uint8_t* data, size_t size) {
    if (!data || size > INT_MAX || size < 3) return {};
    int width = 0, height = 0, channels = 0;
    if (!stbi_info_from_memory(data, static_cast<int>(size), &width, &height, &channels) ||
        width <= 0 || height <= 0 ||
        static_cast<uint64_t>(width) * height > 64ull * 1024 * 1024) return {};
    std::unique_ptr<stbi_uc, decltype(&stbi_image_free)> pixels(
        stbi_load_from_memory(data, static_cast<int>(size), &width, &height, &channels, 4),
        &stbi_image_free);
    if (!pixels) return {};
    DecodedImage image;
    image.width = width;
    image.height = height;
    image.rgba.assign(pixels.get(), pixels.get() + static_cast<size_t>(width) * height * 4);
    return image;
}

} // namespace weva
