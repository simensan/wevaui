#include "../src/image_decode_queue.h"
#include <chrono>
#include <algorithm>
#include <atomic>
#include <cstdlib>
#include <cstdio>
#include <cstdint>
#include <new>
#include <string>
#include <cstring>
#include <array>
#include <cstddef>
#include "tracked_allocations.h"
using weva::test_allocations::live;

static int checks = 0, failures = 0;
static void check(bool passed, const char* what) {
    ++checks;
    if (!passed) { ++failures; std::printf("FAIL %s\n", what); }
}

struct Gate {
    std::mutex mutex;
    std::condition_variable changed;
    int started = 0;
    bool release = false;
};
static weva::DecodedImage blocked_decode(const std::vector<uint8_t>&, void* user) {
    auto& gate = *static_cast<Gate*>(user);
    std::unique_lock<std::mutex> lock(gate.mutex);
    ++gate.started;
    gate.changed.notify_all();
    gate.changed.wait(lock, [&] { return gate.release; });
    return {};
}
int main() {
    Gate gate;
    {
        weva::detail::ImageDecoders queue(blocked_decode, &gate);
        auto busy = std::make_shared<weva::ImageDecodeState>();
        check(queue.submit(busy, "first", {1}), "first worker admitted");
        check(queue.submit(busy, "second", {2}), "second worker admitted");
        {
            std::unique_lock<std::mutex> lock(gate.mutex);
            check(gate.changed.wait_for(lock, std::chrono::seconds(5), [&] { return gate.started == 2; }),
                  "both workers are blocked before cancellation");
        }
        const size_t settled = live.load();
        auto cancelled = std::make_shared<weva::ImageDecodeState>();
        for (int i = 0; i < 4; ++i)
            check(queue.submit(cancelled, std::to_string(i), std::vector<uint8_t>(4 * 1024 * 1024)),
                  "bounded work admitted");
        const size_t queued = live.load();
        check(!queue.has_capacity(cancelled), "queued and running work apply backpressure");
        check(!queue.submit(cancelled, "excess", {3}), "full generation refuses more encoded work");
        cancelled.reset();
        const size_t retained = live.load();
        check(retained <= settled + 64 * 1024, "cancellation releases queued encoded bytes before workers unblock");
        std::printf("image queue: queued %zu bytes, cancelled %zu bytes, settled %zu bytes; retained delta %zu bytes\n",
                    queued, retained, settled, retained > settled ? retained - settled : 0);
        for (int i = 0; i < 128; ++i) {
            auto replaced = std::make_shared<weva::ImageDecodeState>();
            check(queue.submit(replaced, "replacement", std::vector<uint8_t>(64 * 1024)),
                  "replacement generation admitted");
        }
        check(live.load() <= settled + 64 * 1024, "repeated cancellation does not accumulate tickets or payloads");
        std::weak_ptr<weva::ImageDecodeState> retired = busy;
        busy.reset();
        check(retired.expired(), "running decoders do not pin cancelled generation state");
        {
            std::lock_guard<std::mutex> lock(gate.mutex);
            gate.release = true;
        }
        gate.changed.notify_all();
    }
    check(gate.started == 2, "cancelled generation never decodes");
    {
        Gate ready_gate;
        ready_gate.release = true;
        weva::detail::ImageDecoders queue(blocked_decode, &ready_gate);
        auto state = std::make_shared<weva::ImageDecodeState>();
        for (int i = 0; i < 4; ++i)
            check(queue.submit(state, std::to_string(i), {1}), "completion work admitted");
        const auto deadline = std::chrono::steady_clock::now() + std::chrono::seconds(5);
        size_t completed = 0;
        do {
            {
                std::lock_guard<std::mutex> lock(state->mutex);
                completed = state->ready.size();
            }
            if (completed == 4) break;
            std::this_thread::yield();
        } while (std::chrono::steady_clock::now() < deadline);
        check(completed == 4, "all admitted work completes");
        check(!queue.has_capacity(state), "completed but unpublished images still apply backpressure");
        check(!queue.submit(state, "overflow", {2}), "unpublished results cannot grow without bound");
        // Publication is owned by ImageStore, which releases exactly one slot
        // per result moved into its cache.
        if (completed) {
            std::lock_guard<std::mutex> lock(state->mutex);
            state->ready.pop_front();
            --state->outstanding;
        }
        check(queue.has_capacity(state), "publication releases an admission slot");
        check(queue.submit(state, "next", {2}), "deferred work can resume after publication");
    }
    std::printf("image queue memory: %d checks, %d failures\n", checks, failures);
    return failures ? 1 : 0;
}
