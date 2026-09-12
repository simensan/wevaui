cmake_minimum_required(VERSION 3.20)
project(weva LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)

# Keep instrumentation consistent across the core, its tools and embedded ICU.
# These are diagnostic builds; the shipping configuration stays unchanged.
option(WEVA_SANITIZERS "Instrument memory access (and undefined behavior on GCC/Clang)" OFF)
add_library(weva_sanitizers INTERFACE)
if(WEVA_SANITIZERS)
    if(MSVC)
        target_compile_options(weva_sanitizers INTERFACE /fsanitize=address /Zi)
        target_link_options(weva_sanitizers INTERFACE /INCREMENTAL:NO /DEBUG)
        # CMake's default Debug runtime checks conflict with AddressSanitizer.
        # Reject that configuration instead of silently dropping other checks.
        if(CMAKE_BUILD_TYPE STREQUAL "Debug")
            message(FATAL_ERROR "Use Release or RelWithDebInfo for WEVA_SANITIZERS on MSVC")
        endif()
        message(STATUS "Weva sanitizers: AddressSanitizer (MSVC; no UBSan)")
    elseif(CMAKE_CXX_COMPILER_ID MATCHES "^(GNU|Clang|AppleClang)$")
        target_compile_options(weva_sanitizers INTERFACE
            -fsanitize=address,undefined -fno-sanitize-recover=all
            -fno-omit-frame-pointer -g)
        target_link_options(weva_sanitizers INTERFACE -fsanitize=address,undefined)
        message(STATUS "Weva sanitizers: AddressSanitizer and UndefinedBehaviorSanitizer")
    else()
        message(FATAL_ERROR "WEVA_SANITIZERS requires MSVC, GCC or Clang")
    endif()
endif()

# docs/CONVENTIONS.md — these are load-bearing, not stylistic.
#
#   -fno-exceptions : throwing across a GDExtension boundary is undefined.
#                     Errors return weva::Status.
#   NO fast-math    : layout computes in double and the oracle compares
#                     bit-identical geometry against the C# implementation.
#                     -ffast-math / -Ofast silently break that. Do not add them.
add_library(weva_flags INTERFACE)
target_link_libraries(weva_flags INTERFACE weva_sanitizers)
if(MSVC)
    # The same two rules in MSVC's dialect: no exception unwinding, and the
    # standard library told so; warnings at /W4. /utf-8 because the sources
    # hold UTF-8 literals (the UA sheet, test strings).
    target_compile_options(weva_flags INTERFACE /EHs-c- /W4 /utf-8 /wd4100 /wd4127 /wd4244 /wd4267 /wd4456 /wd4457 /wd4458 /wd4459 /wd4702)
    target_compile_definitions(weva_flags INTERFACE _HAS_EXCEPTIONS=0 _CRT_SECURE_NO_WARNINGS NOMINMAX)
else()
    target_compile_options(weva_flags INTERFACE
        -fno-exceptions
        -Wall -Wextra -Wpedantic
        $<$<CONFIG:Debug>:-fno-omit-frame-pointer>
    )
endif()

# A host embeds the core with add_subdirectory and wants neither the tests nor
# the dump tool. Defaulting ON keeps a bare `cmake -S .` building everything.
option(WEVA_BUILD_TESTS "Build the libweva test suite and tools" ON)

add_subdirectory(third_party/icu)
add_subdirectory(libweva)

if(WEVA_BUILD_TESTS)
    enable_testing()
    add_subdirectory(Tools/weva_dump)
    add_subdirectory(Tools/weva_render)
    add_subdirectory(Tools/weva_bench)
    add_subdirectory(Tools/gradsize)
    add_subdirectory(libweva/tests)
    if(WEVA_SANITIZERS)
        add_executable(weva_sanitizer_probe Tools/sanitizer_probe.cpp)
        target_link_libraries(weva_sanitizer_probe PRIVATE weva_sanitizers)
        add_test(NAME weva_asan_active COMMAND ${CMAKE_COMMAND}
            -DPROBE=$<TARGET_FILE:weva_sanitizer_probe> -DMODE=address
            -DWORK=${CMAKE_CURRENT_BINARY_DIR}/sanitizer-probes
            -P ${CMAKE_CURRENT_SOURCE_DIR}/Tools/check_sanitizer.cmake)
        if(NOT MSVC)
            add_test(NAME weva_ubsan_active COMMAND ${CMAKE_COMMAND}
                -DPROBE=$<TARGET_FILE:weva_sanitizer_probe> -DMODE=undefined
                -DWORK=${CMAKE_CURRENT_BINARY_DIR}/sanitizer-probes
                -P ${CMAKE_CURRENT_SOURCE_DIR}/Tools/check_sanitizer.cmake)
        endif()
    endif()
endif()
