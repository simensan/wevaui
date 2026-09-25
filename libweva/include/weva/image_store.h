#pragma once
#include "weva/image_decode.h"

#include <functional>
#include <cstdint>
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
            clear();
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
        clear();
    }

    // Null when the URL cannot be read or is not a format the decoder knows.
    // The caller then paints what it painted before this existed: nothing.
    const DecodedImage* get(std::string_view url);

    // Resolved against the base path, without loading anything. Exposed for
    // tests and for a host that wants to report what it could not find.
    std::string resolve(std::string_view url) const;

    // The bytes at a URL through the same base path and reader an image goes
    // through, undecoded: what an `@import`ed stylesheet needs.
    bool read(std::string_view url, std::vector<uint8_t>* out) const;

    void clear() { cache_.clear(); content_version_ = next_content_version(); }
    // Decoded inputs remain immutable until a reset. Versions are unique
    // across stores too, so a paint cache can safely receive a different one.
    uint64_t content_version() const { return content_version_; }
    size_t size() const { return cache_.size(); }

    // Every URL that was asked for and could not be produced.
    //
    // Worth surfacing rather than swallowing, because the failure mode is
    // silence: a document whose images do not load draws no backgrounds, no
    // <img> and no border images, looks like a page that simply has none, and
    // agrees perfectly with any other engine doing the same. Three separate
    // tools shipped without a base path -- weva_render, weva_dump and the
    // Godot capture scene -- and each was found only when someone eventually
    // looked at a picture. A count nobody has to remember to ask for is
    // cheaper than a fourth.
    std::vector<std::string> missing() const {
        std::vector<std::string> out;
        for (const auto& entry : cache_) {
            if (!entry.second.valid()) out.push_back(entry.first);
        }
        return out;
    }
    size_t missing_count() const {
        size_t n = 0;
        for (const auto& entry : cache_) {
            if (!entry.second.valid()) ++n;
        }
        return n;
    }

    // The directory a document lives in, as its base. The three tools each
    // hand-rolled this split; one of them can now be wrong in one place.
    void set_base_path_from_file(std::string_view file_path) {
        const size_t slash = file_path.find_last_of("/\\");
        set_base_path(slash == std::string_view::npos ? std::string(".")
                                                      : std::string(file_path.substr(0, slash)));
    }

private:
    static uint64_t next_content_version();
    uint64_t content_version_ = next_content_version();
    std::string base_;
    Reader reader_;
    // Keyed by the RESOLVED path, so two stylesheets reaching the same file by
    // different relative routes share one decode.
    std::map<std::string, DecodedImage> cache_;
};

} // namespace weva
