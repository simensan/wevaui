#include "weva/image_store.h"

#include <fstream>
#include <atomic>
#include <condition_variable>
#include <deque>
#include <mutex>
#include <thread>
#include <utility>

namespace weva {

struct ImageDecodeState {
    std::mutex mutex;
    std::deque<std::pair<std::string, DecodedImage>> ready;
};

namespace {

// A bounded number of workers shared by documents. Jobs borrow a generation
// weakly: clear/reload/destruction drops queued work without joining a decoder
// on the render thread. No host callback or DOM state crosses this boundary.
class ImageDecoders {
public:
    ImageDecoders() {
        for (auto& worker : workers_) worker = std::thread([this] { run(); });
    }
    ~ImageDecoders() {
        {
            std::lock_guard<std::mutex> lock(mutex_);
            stopping_ = true;
            jobs_.clear();
        }
        wake_.notify_all();
        for (auto& worker : workers_) worker.join();
    }
    void submit(const std::shared_ptr<ImageDecodeState>& state, std::string path,
                std::vector<uint8_t> bytes) {
        {
            std::lock_guard<std::mutex> lock(mutex_);
            jobs_.push_back({state, std::move(path), std::move(bytes)});
        }
        wake_.notify_one();
    }
private:
    struct Job {
        std::weak_ptr<ImageDecodeState> state;
        std::string path;
        std::vector<uint8_t> bytes;
    };
    void run() {
        for (;;) {
            Job job;
            {
                std::unique_lock<std::mutex> lock(mutex_);
                wake_.wait(lock, [this] { return stopping_ || !jobs_.empty(); });
                if (stopping_) return;
                job = std::move(jobs_.front());
                jobs_.pop_front();
            }
            if (job.state.expired()) continue;
            DecodedImage image = decode_image(job.bytes.data(), job.bytes.size());
            prepare_image_sampling(image);
            if (const auto state = job.state.lock()) {
                std::lock_guard<std::mutex> lock(state->mutex);
                state->ready.emplace_back(std::move(job.path), std::move(image));
            }
        }
    }
    std::thread workers_[2];
    std::mutex mutex_;
    std::condition_variable wake_;
    std::deque<Job> jobs_;
    bool stopping_ = false;
};

ImageDecoders& image_decoders() {
    static ImageDecoders workers;
    return workers;
}

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

ImageStore::~ImageStore() = default;
ImageStore::ImageStore(ImageStore&&) noexcept = default;
ImageStore& ImageStore::operator=(ImageStore&&) noexcept = default;

void ImageStore::clear() {
    async_.reset();
    pending_.clear();
    requests_.clear();
    cache_.clear();
    generation_version_ = content_version_ = next_content_version();
}

bool ImageStore::poll() {
    if (!async_ || pending_.empty()) return false;
    // Admission/publication are bounded too: decoding off-thread alone still
    // lets an icon grid read all its files, or downsample all completed images,
    // on one frame. Readers remain on their owning host thread.
    constexpr size_t batch = 2;
    for (size_t i = 0; i < batch && !requests_.empty(); ++i) {
        std::string path = std::move(requests_.front());
        requests_.pop_front();
        std::vector<uint8_t> bytes;
        const bool read = reader_ ? reader_(path, &bytes) : read_file(path, &bytes);
        if (read && !bytes.empty()) image_decoders().submit(async_, std::move(path), std::move(bytes));
        else {
            pending_.erase(path);
            cache_.emplace(std::move(path), DecodedImage{});
        }
    }
    std::deque<std::pair<std::string, DecodedImage>> ready;
    {
        std::lock_guard<std::mutex> lock(async_->mutex);
        for (size_t i = 0; i < batch && !async_->ready.empty(); ++i) {
            ready.push_back(std::move(async_->ready.front()));
            async_->ready.pop_front();
        }
    }
    bool changed = false;
    for (auto& entry : ready) {
        pending_.erase(entry.first);
        changed = changed || entry.second.valid();
        cache_.emplace(std::move(entry.first), std::move(entry.second));
    }
    if (changed) content_version_ = next_content_version();
    return changed;
}

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

bool ImageStore::read(std::string_view url, std::vector<uint8_t>* out) const {
    const std::string path = resolve(url);
    return reader_ ? reader_(path, out) : read_file(path, out);
}

const DecodedImage* ImageStore::get(std::string_view url, bool async) {
    if (url.empty()) return nullptr;
    const std::string path = resolve(url);

    const auto it = cache_.find(path);
    if (it != cache_.end()) {
        // A cached failure is an empty image, and answering with null is what
        // stops the next frame trying the same missing file again.
        return it->second.valid() ? &it->second : nullptr;
    }
    if (pending_.count(path)) return nullptr;

    if (async) {
        if (!async_) async_ = std::make_shared<ImageDecodeState>();
        pending_.insert(path);
        requests_.push_back(path);
        return nullptr;
    }
    std::vector<uint8_t> bytes;
    const bool read = reader_ ? reader_(path, &bytes) : read_file(path, &bytes);
    DecodedImage image;
    if (read && !bytes.empty()) image = decode_image(bytes.data(), bytes.size());

    const auto inserted = cache_.emplace(path, std::move(image)).first;
    return inserted->second.valid() ? &inserted->second : nullptr;
}

} // namespace weva
