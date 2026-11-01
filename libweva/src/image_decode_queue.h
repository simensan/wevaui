#pragma once
// Private async scheduler contract, also used by its blocked-worker regression.
#include "weva/image_decode.h"
#include <condition_variable>
#include <deque>
#include <memory>
#include <mutex>
#include <string>
#include <thread>
#include <utility>
#include <vector>

namespace weva {
struct ImageDecodeJob {
    std::string path;
    std::vector<uint8_t> bytes;
};

// The generation owns queued bytes; workers borrow it only to take/publish work.
// Dropping it releases queued and completed payloads without joining a decoder.
struct ImageDecodeState {
    std::mutex mutex;
    std::deque<ImageDecodeJob> queued;
    size_t outstanding = 0; // queued + decoding + completed but unpublished
    std::deque<std::pair<std::string, DecodedImage>> ready;
};

namespace detail {
class ImageDecoders {
public:
    using Decoder = DecodedImage (*)(const std::vector<uint8_t>&, void*);
    ImageDecoders(Decoder decoder, void* user);
    ~ImageDecoders();
    static bool has_capacity(const std::shared_ptr<ImageDecodeState>& state);
    bool submit(const std::shared_ptr<ImageDecodeState>& state, std::string path,
                std::vector<uint8_t> bytes);
private:
    void run();
    std::thread workers_[2];
    std::mutex mutex_;
    std::condition_variable wake_;
    // Expired generation tickets never own encoded/decoded payloads.
    std::deque<std::weak_ptr<ImageDecodeState>> jobs_;
    bool stopping_ = false;
    Decoder decoder_;
    void* user_;
};
} // namespace detail
} // namespace weva
