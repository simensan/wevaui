cmake_minimum_required(VERSION 3.20)
project(weva LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)

# docs/CONVENTIONS.md — these are load-bearing, not stylistic.
#
#   -fno-exceptions : throwing across a GDExtension boundary is undefined.
#                     Errors return weva::Status.
#   NO fast-math    : layout computes in double and the oracle compares
#                     bit-identical geometry against the C# implementation.
#                     -ffast-math / -Ofast silently break that. Do not add them.
add_library(weva_flags INTERFACE)
target_compile_options(weva_flags INTERFACE
    -fno-exceptions
    -Wall -Wextra -Wpedantic
    $<$<CONFIG:Debug>:-fno-omit-frame-pointer>
)

add_subdirectory(libweva)
add_subdirectory(tools/weva_dump)

enable_testing()
add_subdirectory(libweva/tests)
