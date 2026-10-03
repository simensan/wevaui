#pragma once
// Fork-join over index ranges, for the rasterizers' independent rows.
//
// Threads are started for one call and joined before it returns: a pool that
// outlived the call would have to be torn down by a static destructor, which
// deadlocks under the Windows loader lock and crashes a Godot extension that
// is hot-reloaded while its workers sleep. Starting three threads costs tens
// of microseconds against the milliseconds of a job worth splitting, so
// callers only split work above a size threshold.
//
// Every index is computed by exactly one thread with the arithmetic the
// serial loop uses, so the output is byte-identical to a serial run.

#include <algorithm>
#include <atomic>
#include <cstdlib>
#include <thread>
#include <vector>

namespace weva {

// A test's override of the thread count; 0 leaves it to the default.
inline std::atomic<int>& raster_thread_override() {
    static std::atomic<int> value{0};
    return value;
}

// Set while a thread runs one of several raster jobs side by side
// (run_raster_jobs in paint.cpp). Every thread is busy then, so a job keeps
// its rows to itself rather than starting threads of its own.
inline bool& raster_job_worker() {
    static thread_local bool value = false;
    return value;
}

// Threads a raster job may use, the caller's included. WEVA_RASTER_THREADS
// overrides it; 1 keeps every job on the calling thread.
inline int raster_thread_count() {
    if (raster_job_worker()) return 1;
    if (const int forced = raster_thread_override().load(std::memory_order_relaxed)) return forced;
    static const int count = [] {
        if (const char* env = std::getenv("WEVA_RASTER_THREADS")) {
            const int n = std::atoi(env);
            return n < 1 ? 1 : std::min(n, 16);
        }
        const unsigned hw = std::thread::hardware_concurrency();
        return hw <= 1 ? 1 : static_cast<int>(std::min(hw, 4u));
    }();
    return count;
}

// Calls fn(begin, end) over [0, count) split into contiguous ranges, each a
// multiple of `grain` long but for the last. Runs on the calling thread alone
// when `work` (the caller's estimate of the job's cost) is below
// `min_work`, or when only one thread is allowed.
template <typename Fn>
void parallel_ranges(int count, int grain, long long work, long long min_work, Fn&& fn) {
    if (count <= 0) return;
    int threads = raster_thread_count();
    if (grain < 1) grain = 1;
    const int chunks = (count + grain - 1) / grain;
    threads = std::min(threads, chunks);
    if (threads <= 1 || work < min_work) {
        fn(0, count);
        return;
    }
    const int per = (chunks + threads - 1) / threads * grain;
    std::vector<std::thread> workers;
    workers.reserve(static_cast<size_t>(threads - 1));
    for (int t = 1; t < threads; ++t) {
        const int begin = std::min(count, t * per);
        const int end = std::min(count, begin + per);
        if (begin >= end) break;
        workers.emplace_back([&fn, begin, end] { fn(begin, end); });
    }
    fn(0, std::min(count, per));
    for (std::thread& w : workers) w.join();
}

} // namespace weva
