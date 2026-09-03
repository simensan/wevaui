#pragma once
#include "weva/image_decode.h"

#include <functional>
#include <map>
#include <string>
#include <string_view>

namespace weva {

// Decoded images, kept for the life of the document and looked up by the URL
// the stylesheet wrote.
//
// The cache is the point rather than a nicety: a background-image is
// rasterized into its box's texture on every layout that changes the box, and
// decoding a PNG per frame would be the most expensive thing in the engine.
// Failures are cached too -- a stylesheet that names a file that is not there
// should not reopen it sixty times a second.
class ImageStore {
public:
    // What a relative `url(...)` is relative TO. The document's own location,
    // normally, so `url(icons/gem.png)` beside the HTML resolves the way a
    // browser resolves it.
    void set_base_path(std::string base) {
        if (base != base_) {
            base_ = std::move(base);
            cache_.clear();
        }
    }
    const std::string& base_path() const { return base_; }

    // How bytes are obtained for a resolved path. The default reads the
    // filesystem; a host whose assets are not files -- Godot's `res://` inside
    // an exported .pck, a Unity addressable -- replaces it, and STILL gets the
    // core's decoder, so both backends see identical pixels.
    using Reader = std::function<bool(const std::string& path, std::vector<uint8_t>* out)>;
    void set_reader(Reader reader) {
        reader_ = std::move(reader);
        cache_.clear();
    }

    // Null when the URL cannot be read or is not a format the decoder knows.
    // The caller then paints what it painted before this existed: nothing.
    const DecodedImage* get(std::string_view url);

    // Resolved against the base path, without loading anything. Exposed for
    // tests and for a host that wants to report what it could not find.
    std::string resolve(std::string_view url) const;

    void clear() { cache_.clear(); }
    size_t size() const { return cache_.size(); }

private:
    std::string base_;
    Reader reader_;
    // Keyed by the RESOLVED path, so two stylesheets reaching the same file by
    // different relative routes share one decode.
    std::map<std::string, DecodedImage> cache_;
};

} // namespace weva
