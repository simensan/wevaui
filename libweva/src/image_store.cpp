#include "weva/image_store.h"
#include "image_decode_queue.h"

#include <algorithm>
#include <fstream>
#include <atomic>
#include <condition_variable>
#include <deque>
#include <mutex>
#include <thread>
#include <utility>

namespace weva {

namespace detail {

ImageDecoders::ImageDecoders(Decoder decoder, void* user) : decoder_(decoder), user_(user) {
    for (auto& worker : workers_) worker = std::thread([this] { run(); });
}

ImageDecoders::~ImageDecoders() {
    {
        std::lock_guard<std::mutex> lock(mutex_);
        stopping_ = true;
        jobs_.clear();
    }
    wake_.notify_all();
    for (auto& worker : workers_) worker.join();
}

namespace { constexpr size_t kOutstandingPerGeneration = 4; }

bool ImageDecoders::has_capacity(const std::shared_ptr<ImageDecodeState>& state) {
    std::lock_guard<std::mutex> lock(state->mutex);
    return state->outstanding < kOutstandingPerGeneration;
}

bool ImageDecoders::submit(const std::shared_ptr<ImageDecodeState>& state, std::string path,
                           std::vector<uint8_t> bytes) {
    {
        std::lock_guard<std::mutex> lock(state->mutex);
        if (state->outstanding >= kOutstandingPerGeneration) return false;
        state->queued.push_back({std::move(path), std::move(bytes)});
        ++state->outstanding;
    }
    {
        std::lock_guard<std::mutex> lock(mutex_);
        // Do not accumulate cancelled tickets while a large decode occupies
        // both workers and screens continue to be replaced.
        jobs_.erase(std::remove_if(jobs_.begin(), jobs_.end(),
                                  [](const auto& ticket) { return ticket.expired(); }), jobs_.end());
        jobs_.push_back(state);
    }
    wake_.notify_one();
    return true;
}

void ImageDecoders::run() {
    for (;;) {
        std::weak_ptr<ImageDecodeState> ticket;
        {
            std::unique_lock<std::mutex> lock(mutex_);
            wake_.wait(lock, [this] { return stopping_ || !jobs_.empty(); });
            if (stopping_) return;
            ticket = std::move(jobs_.front());
            jobs_.pop_front();
        }
        ImageDecodeJob job;
        {
            const auto state = ticket.lock();
            if (!state) continue;
            std::lock_guard<std::mutex> lock(state->mutex);
            job = std::move(state->queued.front());
            state->queued.pop_front();
        }
        // No strong generation reference crosses the potentially long decode.
        DecodedImage image = decoder_(job.bytes, user_);
        if (const auto state = ticket.lock()) {
            std::lock_guard<std::mutex> lock(state->mutex);
            state->ready.emplace_back(std::move(job.path), std::move(image));
        }
    }
}

} // namespace detail

namespace {

DecodedImage decode_prepared(const std::vector<uint8_t>& bytes, void*) {
    DecodedImage image = decode_image(bytes.data(), bytes.size());
    prepare_image_sampling(image);
    return image;
}

std::shared_ptr<detail::ImageDecoders> image_decoders() {
    static std::mutex mutex;
    static std::weak_ptr<detail::ImageDecoders> shared;
    std::lock_guard<std::mutex> lock(mutex);
    auto workers = shared.lock();
    if (!workers) {
        workers = std::make_shared<detail::ImageDecoders>(decode_prepared, nullptr);
        shared = workers;
    }
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

ImageStore::~ImageStore() {
    // Cancel payloads before releasing the last worker-pool lease. The lease
    // joins at most the running decodes here, outside the DLL loader lock.
    async_.reset();
}
ImageStore::ImageStore(ImageStore&& other) noexcept : ImageStore() { swap(other); }
ImageStore& ImageStore::operator=(ImageStore&& other) noexcept {
    if (this != &other) {
        ImageStore moved(std::move(other));
        swap(moved);
    }
    return *this;
}

void ImageStore::swap(ImageStore& other) noexcept {
    using std::swap;
    swap(cached_bytes_, other.cached_bytes_); swap(pending_bytes_, other.pending_bytes_);
    swap(use_serial_, other.use_serial_); swap(trim_dirty_, other.trim_dirty_);
    swap(generation_version_, other.generation_version_); swap(content_version_, other.content_version_);
    swap(base_, other.base_); swap(reader_, other.reader_); swap(cache_, other.cache_);
    swap(pending_, other.pending_); swap(requests_, other.requests_);
    swap(async_, other.async_); swap(decoders_, other.decoders_);
}

size_t ImageStore::charge(const Cache::value_type& entry) {
    return sizeof(Cache::value_type) + 4 * sizeof(void*) + entry.first.capacity() +
           entry.second.image.rgba.capacity() + entry.second.image.block_sums.capacity() * sizeof(uint64_t);
}

const DecodedImage* ImageStore::publish(std::string path, DecodedImage image) {
    const auto inserted = cache_.emplace(std::move(path), Entry{std::move(image), ++use_serial_});
    auto& entry = *inserted.first;
    if (inserted.second) {
        if (entry.second.image.valid()) entry.second.image.resource_version = next_content_version();
        cached_bytes_ += charge(entry);
        trim_dirty_ = true;
    }
    return entry.second.image.valid() ? &entry.second.image : nullptr;
}

void ImageStore::erase_pending(const std::string& path) {
    const auto found = pending_.find(path);
    if (found == pending_.end()) return;
    pending_bytes_ -= sizeof(std::string) + 4 * sizeof(void*) + found->capacity();
    pending_.erase(found);
}

void ImageStore::trim_unused(const std::set<std::string>& live_urls, size_t bytes, size_t entries) {
    // Discard unadmitted obsolete reads even when no worker is available.
    requests_.erase(std::remove_if(requests_.begin(), requests_.end(), [&](const std::string& path) {
        if (live_urls.count(path)) return false;
        erase_pending(path);
        pending_bytes_ -= sizeof(std::string) + path.capacity();
        return true;
    }), requests_.end());
    std::vector<Cache::iterator> candidates;
    for (auto it = cache_.begin(); it != cache_.end(); ++it)
        if (!live_urls.count(it->first)) candidates.push_back(it);
    std::sort(candidates.begin(), candidates.end(), [](auto a, auto b) {
        return a->second.used < b->second.used;
    });
    for (const auto entry : candidates) {
        if (retained_bytes() <= bytes && cache_.size() + pending_.size() <= entries) break;
        cached_bytes_ -= charge(*entry);
        cache_.erase(entry);
    }
    trim_dirty_ = false;
}

void ImageStore::clear() {
    async_.reset();
    pending_.clear();
    requests_.clear();
    cache_.clear();
    cached_bytes_ = pending_bytes_ = 0;
    use_serial_ = 0; trim_dirty_ = false;
    generation_version_ = content_version_ = next_content_version();
}

bool ImageStore::poll() {
    if (!async_ || pending_.empty()) return false;
    // Admission/publication are bounded too: decoding off-thread alone still
    // lets an icon grid read all its files, or downsample all completed images,
    // on one frame. Readers remain on their owning host thread.
    constexpr size_t batch = 2;
    for (size_t i = 0; i < batch && !requests_.empty(); ++i) {
        // Backpressure before invoking the host reader: inactive/slow
        // generations may retain at most four encoded, decoding or ready jobs.
        if (!detail::ImageDecoders::has_capacity(async_)) break;
        std::string path = requests_.front();
        std::vector<uint8_t> bytes;
        const bool read = reader_ ? reader_(path, &bytes) : read_file(path, &bytes);
        if (read && !bytes.empty()) {
            if (!decoders_) decoders_ = image_decoders();
            if (!decoders_->submit(async_, path, std::move(bytes))) break;
        } else {
            erase_pending(path);
            publish(path, DecodedImage{});
        }
        pending_bytes_ -= sizeof(std::string) + requests_.front().capacity();
        requests_.pop_front();
    }
    std::deque<std::pair<std::string, DecodedImage>> ready;
    {
        std::lock_guard<std::mutex> lock(async_->mutex);
        for (size_t i = 0; i < batch && !async_->ready.empty(); ++i) {
            ready.push_back(std::move(async_->ready.front()));
            async_->ready.pop_front();
            --async_->outstanding;
        }
    }
    bool changed = false;
    for (auto& entry : ready) {
        erase_pending(entry.first);
        changed = changed || entry.second.valid();
        publish(std::move(entry.first), std::move(entry.second));
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

bool ImageStore::same_intrinsic_dimensions(std::string_view a, std::string_view b) const {
    if (a.empty() || b.empty()) return false;
    const auto first = cache_.find(resolve(a)), second = cache_.find(resolve(b));
    return first != cache_.end() && second != cache_.end() &&
        first->second.image.valid() && second->second.image.valid() &&
        first->second.image.width == second->second.image.width &&
        first->second.image.height == second->second.image.height;
}

const DecodedImage* ImageStore::get(std::string_view url, bool async) {
    if (url.empty()) return nullptr;
    const std::string path = resolve(url);

    const auto it = cache_.find(path);
    if (it != cache_.end()) {
        // A cached failure is an empty image, and answering with null is what
        // stops the next frame trying the same missing file again.
        it->second.used = ++use_serial_;
        trim_dirty_ = true;
        return it->second.image.valid() ? &it->second.image : nullptr;
    }
    if (pending_.count(path)) return nullptr;

    if (async) {
        if (!async_) async_ = std::make_shared<ImageDecodeState>();
        const auto inserted = pending_.insert(path).first;
        requests_.push_back(path);
        pending_bytes_ += sizeof(std::string) + 4 * sizeof(void*) + inserted->capacity() +
                          sizeof(std::string) + requests_.back().capacity();
        trim_dirty_ = true;
        return nullptr;
    }
    std::vector<uint8_t> bytes;
    const bool read = reader_ ? reader_(path, &bytes) : read_file(path, &bytes);
    DecodedImage image;
    if (read && !bytes.empty()) image = decode_image(bytes.data(), bytes.size());

    return publish(path, std::move(image));
}

} // namespace weva
