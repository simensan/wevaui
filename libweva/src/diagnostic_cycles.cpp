#include "diagnostic_cycles.h"
#ifdef _WIN32
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#endif
namespace weva {
uint64_t diagnostic_thread_cycles() noexcept {
#ifdef _WIN32
    ULONG64 cycles = 0;
    if (QueryThreadCycleTime(GetCurrentThread(), &cycles)) return cycles;
#endif
    return 0;
}
}
