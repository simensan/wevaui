#include "weva/image_store.h"

#include <fstream>
#include <atomic>

namespace weva {

namespace {

bool has_scheme(std::string_view url) {
    // `res://`, `user://`, `http://`, `data:` -- anything a host might own.
    const size_t colon = url.find(':');
    if (colon == std::string_view::npos || colon == 0) return false;
    for (size_t i = 0; i < colon; ++i) {
        const char c = url[i];
        const bool ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') ||
                        (c >= '0' && c <= '9') || c == '+' || c == '-' || c == '.';
        if (!ok) return false;
    }
    return true;
}

bool is_absolute(std::string_view path) {
    if (path.empty()) return false;
    if (path.front() == '/' || path.front() == '\\') return true;
    // A Windows drive letter.
    return path.size() > 2 && path[1] == ':' && (path[2] == '/' || path[2] == '\\');
}

bool read_file(const std::string& path, std::vector<uint8_t>* out) {
    std::ifstream in(path, std::ios::binary);
    if (!in) return false;
    in.seekg(0, std::ios::end);
    const std::streamoff size = in.tellg();
    if (size < 0) return false;
    in.seekg(0, std::ios::beg);
    out->resize(static_cast<size_t>(size));
    if (size > 0) in.read(reinterpret_cast<char*>(out->data()), size);
    return static_cast<bool>(in);
}

} // namespace

uint64_t ImageStore::next_content_version() {
    static std::atomic<uint64_t> serial{1};
    return serial.fetch_add(1, std::memory_order_relaxed);
}

std::string ImageStore::resolve(std::string_view url) const {
    // A scheme or an absolute path is already what it is; joining a base onto
    // `res://icons/gem.png` would produce something no host could open.
    if (url.empty() || has_scheme(url) || is_absolute(url) || base_.empty()) {
        return std::string(url);
    }
    std::string out = base_;
    if (out.back() != '/' && out.back() != '\\') out.push_back('/');
    out.append(url);
    return out;
}

const DecodedImage* ImageStore::get(std::string_view url) {
    if (url.empty()) return nullptr;
    const std::string path = resolve(url);

    const auto it = cache_.find(path);
    if (it != cache_.end()) {
        // A cached failure is an empty image, and answering with null is what
        // stops the next frame trying the same missing file again.
        return it->second.valid() ? &it->second : nullptr;
    }

    std::vector<uint8_t> bytes;
    const bool read = reader_ ? reader_(path, &bytes) : read_file(path, &bytes);
    DecodedImage image;
    if (read && !bytes.empty()) image = decode_image(bytes.data(), bytes.size());

    const auto inserted = cache_.emplace(path, std::move(image)).first;
    return inserted->second.valid() ? &inserted->second : nullptr;
}

} // namespace weva
