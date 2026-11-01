#pragma once
#include "weva/image_decode.h"

#include <functional>
#include <cstdint>
#include <deque>
#include <map>
#include <memory>
#include <set>
#include <string>
#include <string_view>

namespace weva {
struct ImageDecodeState;
namespace detail { class ImageDecoders; }

// Decoded images, cached by resolved URL. The document trims unused sources
// only after layout and all raster jobs release their borrowed pointers.
//
// The cache is the point rather than a nicety: a background-image is
// rasterized into its box's texture on every layout that changes the box, and
// decoding a PNG per frame would be the most expensive thing in the engine.
// Failures are cached too -- a stylesheet that names a file that is not there
// should not reopen it sixty times a second.
class ImageStore {
public:
    ImageStore() = default;
    ~ImageStore();
    ImageStore(ImageStore&&) noexcept;
    ImageStore& operator=(ImageStore&&) noexcept;
    ImageStore(const ImageStore&) = delete;
    ImageStore& operator=(const ImageStore&) = delete;
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
    // `decoding="async"` opts an <img> into deferred decoding. Readers stay
    // on the calling thread; workers own only bytes and decoded pixels.
    // Returned pixels remain valid until clear() or an explicit trim boundary.
    const DecodedImage* get(std::string_view url, bool async = false);
    // A source swap between published images with equal natural dimensions
    // changes pixels, not layout inputs. Never starts a read or decode.
    bool same_intrinsic_dimensions(std::string_view a, std::string_view b) const;
    // Publish finished images on the document thread. True requires layout
    // (intrinsic dimensions may have changed) and repaint.
    bool poll();
    bool pending() const { return !pending_.empty(); }

    // Resolved against the base path, without loading anything. Exposed for
    // tests and for a host that wants to report what it could not find.
    std::string resolve(std::string_view url) const;

    // The bytes at a URL through the same base path and reader an image goes
    // through, undecoded: what an `@import`ed stylesheet needs.
    bool read(std::string_view url, std::vector<uint8_t>* out) const;

    void clear();
    // Reset/deferred-publication notification. Eviction changes no live pixels.
    uint64_t content_version() const { return content_version_; }
    // Reader/base-path epoch. Each published image's resource_version also
    // distinguishes eviction/reload within this epoch without invalidating
    // unrelated filtered pixels.
    uint64_t generation_version() const { return generation_version_; }
    size_t size() const { return cache_.size(); }
    static constexpr size_t kCacheBytes = 64 * 1024 * 1024;
    static constexpr size_t kCacheEntries = 1024;
    // Charged vector capacity, URL storage and entry metadata, not allocator/RSS.
    size_t retained_bytes() const { return cached_bytes_ + pending_bytes_; }
    bool needs_trim() const {
        return trim_dirty_ && (retained_bytes() > kCacheBytes ||
                               cache_.size() + pending_.size() > kCacheEntries);
    }
    void request_trim() { trim_dirty_ = true; }
    // Must run after all borrowed images/raster jobs are finished. Current box
    // references are pinned even when their live working set exceeds the budget.
    // Unreferenced queued URLs are cancelled before they invoke a host reader.
    void trim_unused(const std::set<std::string>& live_resolved_urls,
                     size_t bytes = kCacheBytes, size_t entries = kCacheEntries);

    // Retained failed URLs, including every currently referenced failure.
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
            if (!entry.second.image.valid()) out.push_back(entry.first);
        }
        return out;
    }
    size_t missing_count() const {
        size_t n = 0;
        for (const auto& entry : cache_) {
            if (!entry.second.image.valid()) ++n;
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
    struct Entry {
        DecodedImage image;
        uint64_t used = 0;
    };
    using Cache = std::map<std::string, Entry>;
    static uint64_t next_content_version();
    static size_t charge(const Cache::value_type& entry);
    const DecodedImage* publish(std::string path, DecodedImage image);
    void erase_pending(const std::string& path);
    void swap(ImageStore& other) noexcept;
    size_t cached_bytes_ = 0, pending_bytes_ = 0;
    uint64_t use_serial_ = 0;
    bool trim_dirty_ = false;
    uint64_t generation_version_ = next_content_version();
    uint64_t content_version_ = generation_version_;
    std::string base_;
    Reader reader_;
    // Keyed by the RESOLVED path, so two stylesheets reaching the same file by
    // different relative routes share one decode.
    Cache cache_;
    std::set<std::string> pending_;
    std::deque<std::string> requests_;
    std::shared_ptr<ImageDecodeState> async_;
    // Keep the shared pool on live stores, never on a process-global owner.
    // The final store joins active decodes before a host can unload core code.
    std::shared_ptr<detail::ImageDecoders> decoders_;
};

} // namespace weva
