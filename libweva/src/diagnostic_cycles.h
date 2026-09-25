#pragma once
#include <cstdint>
namespace weva {
// Zero means unavailable. Raw executing-thread cycles, never elapsed time.
uint64_t diagnostic_thread_cycles() noexcept;
}
