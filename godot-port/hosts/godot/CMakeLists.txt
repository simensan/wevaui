# The Godot host: a GDExtension shared library binding libweva's C ABI.
#
# godot-cpp is not vendored. Point GODOT_CPP_DIR at a built checkout:
#
#   cmake -S hosts/godot -B build-godot -DGODOT_CPP_DIR=/path/to/godot-cpp
#
# Keeping it external means the core builds and tests without any Godot
# dependency at all, which is what lets libweva stay host-agnostic.

cmake_minimum_required(VERSION 3.16)
project(weva_godot CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_POSITION_INDEPENDENT_CODE ON)

if(NOT DEFINED GODOT_CPP_DIR)
    message(FATAL_ERROR "Set GODOT_CPP_DIR to a built godot-cpp checkout.")
endif()

# The core, built from source alongside the host so a change to either is one
# build away from being testable. Tests are off here: they belong to the core's
# own build, and the host has its own end-to-end checks in project/.
set(WEVA_BUILD_TESTS OFF CACHE BOOL "" FORCE)
add_subdirectory(${CMAKE_CURRENT_SOURCE_DIR}/../.. ${CMAKE_CURRENT_BINARY_DIR}/weva-core)

add_library(weva_godot SHARED
    src/register_types.cpp
    src/weva_node.cpp
)

# godot-cpp's bindings are generated at build time, so half its headers live in
# the source tree and half under whichever directory it was built in. SCons
# writes them to <src>/gen/include; the CMake build writes them to
# <build>/gen/include. Both are offered rather than guessing which was used.
if(NOT DEFINED GODOT_CPP_BUILD_DIR)
    set(GODOT_CPP_BUILD_DIR "${GODOT_CPP_DIR}/build")
endif()

target_include_directories(weva_godot PRIVATE
    ${GODOT_CPP_DIR}/include
    ${GODOT_CPP_DIR}/gen/include
    ${GODOT_CPP_BUILD_DIR}/gen/include
    ${GODOT_CPP_DIR}/gdextension
)

# The artifact name carries platform, configuration and word size
# (libgodot-cpp.linux.release.64.a), and the directory differs between the SCons
# and CMake builds, so neither a fixed name nor a fixed path finds it. Glob the
# two known output directories instead and take what is there.
file(GLOB GODOT_CPP_CANDIDATES
    "${GODOT_CPP_BUILD_DIR}/bin/libgodot-cpp*.a"
    "${GODOT_CPP_BUILD_DIR}/libgodot-cpp*.a"
    "${GODOT_CPP_DIR}/bin/libgodot-cpp*.a"
)
list(GET GODOT_CPP_CANDIDATES 0 GODOT_CPP_LIB)
if(NOT GODOT_CPP_LIB)
    message(FATAL_ERROR
        "godot-cpp library not found under ${GODOT_CPP_DIR} or ${GODOT_CPP_BUILD_DIR}. "
        "Build it first, or pass -DGODOT_CPP_BUILD_DIR=<its build directory>.")
endif()
message(STATUS "godot-cpp: ${GODOT_CPP_LIB}")

target_link_libraries(weva_godot PRIVATE weva ${GODOT_CPP_LIB})

# The engine loads the library by the name in weva.gdextension, so the output
# goes straight into the demo project's addons directory.
set_target_properties(weva_godot PROPERTIES
    OUTPUT_NAME "weva_godot"
    LIBRARY_OUTPUT_DIRECTORY "${CMAKE_CURRENT_SOURCE_DIR}/project/addons/weva/bin"
)
