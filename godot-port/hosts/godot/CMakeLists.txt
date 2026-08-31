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

target_include_directories(weva_godot PRIVATE
    ${GODOT_CPP_DIR}/include
    ${GODOT_CPP_DIR}/gen/include
    ${GODOT_CPP_DIR}/gdextension
)

find_library(GODOT_CPP_LIB
    NAMES godot-cpp libgodot-cpp
    PATHS ${GODOT_CPP_DIR}/build ${GODOT_CPP_DIR}/bin
    NO_DEFAULT_PATH
)
if(NOT GODOT_CPP_LIB)
    message(FATAL_ERROR "godot-cpp library not found under ${GODOT_CPP_DIR}. Build it first.")
endif()

target_link_libraries(weva_godot PRIVATE weva ${GODOT_CPP_LIB})

# The engine loads the library by the name in weva.gdextension, so the output
# goes straight into the demo project's addons directory.
set_target_properties(weva_godot PROPERTIES
    OUTPUT_NAME "weva_godot"
    LIBRARY_OUTPUT_DIRECTORY "${CMAKE_CURRENT_SOURCE_DIR}/project/addons/weva/bin"
)
