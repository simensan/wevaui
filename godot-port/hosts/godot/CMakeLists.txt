# The Godot host: a GDExtension shared library binding libweva's C ABI.
#
# godot-cpp is not vendored. Point GODOT_CPP_DIR at its source checkout:
#
#   cmake -S hosts/godot -B build-godot -DGODOT_CPP_DIR=/path/to/godot-cpp
#
# Keeping it external means the core builds and tests without any Godot
# dependency at all, which is what lets libweva stay host-agnostic.

cmake_minimum_required(VERSION 3.20)
# godot-cpp links the static CRT on Windows (/MT); the extension and the core
# must match it, and a GDExtension that carries no CRT dependency is the
# right shape anyway. CMP0091 lets the variable choose it (before project()).
cmake_policy(SET CMP0091 NEW)
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded" CACHE STRING "MSVC runtime shared by the host, core and godot-cpp")
project(weva_godot CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_POSITION_INDEPENDENT_CODE ON)

if(NOT DEFINED GODOT_CPP_DIR)
    message(FATAL_ERROR "Set GODOT_CPP_DIR to a godot-cpp source checkout.")
endif()
if(NOT EXISTS "${GODOT_CPP_DIR}/CMakeLists.txt")
    message(FATAL_ERROR "No godot-cpp CMakeLists.txt under GODOT_CPP_DIR=${GODOT_CPP_DIR}")
endif()

# Import the real target so its generated headers, precision, method binding,
# threading and debug definitions travel with the archive. Linking an arbitrary
# prebuilt archive without these requirements crashed fresh Windows imports.
set(GODOTCPP_API_VERSION "4.7" CACHE STRING "Godot API version for the bindings")
add_subdirectory("${GODOT_CPP_DIR}" "${CMAKE_CURRENT_BINARY_DIR}/godot-cpp" EXCLUDE_FROM_ALL)
if(DEFINED GODOT_CPP_BUILD_DIR)
    message(STATUS "GODOT_CPP_BUILD_DIR is obsolete; godot-cpp builds alongside the host.")
endif()

# The core, built from source alongside the host so a change to either is one
# build away from being testable. Tests are off here: they belong to the core's
# own build, and the host has its own end-to-end checks in project/.
set(WEVA_BUILD_TESTS OFF CACHE BOOL "" FORCE)
add_subdirectory(${CMAKE_CURRENT_SOURCE_DIR}/../.. ${CMAKE_CURRENT_BINARY_DIR}/weva-core)

add_library(weva_godot SHARED
    src/godot_font.cpp
    src/register_types.cpp
    src/weva_node.cpp
)

# ICU character properties mirror the engine's script iterator (godot_font.cpp).
target_link_libraries(weva_godot PRIVATE weva godot-cpp weva_icu_common)
# Stamp the linked binary, not a developer-supplied label. Packaging checks
# both this binary digest and the current source/dependency content digests.
find_package(Python3 3.9 REQUIRED COMPONENTS Interpreter)
add_custom_command(TARGET weva_godot POST_BUILD
    COMMAND "${Python3_EXECUTABLE}" "${CMAKE_CURRENT_SOURCE_DIR}/build_metadata.py"
        --library "$<TARGET_FILE:weva_godot>"
        --source-dir "${CMAKE_CURRENT_SOURCE_DIR}/../.."
        --godot-cpp-dir "${GODOT_CPP_DIR}"
        --configuration "$<CONFIG>"
        --compiler "${CMAKE_CXX_COMPILER_ID}"
        --compiler-version "${CMAKE_CXX_COMPILER_VERSION}"
        --cmake-version "${CMAKE_VERSION}"
        --godot-api "${GODOTCPP_API_VERSION}"
        --precision "${GODOTCPP_PRECISION}"
        --custom-api "${GODOTCPP_CUSTOM_API_FILE}"
        --sanitizers "${WEVA_SANITIZERS}"
    VERBATIM)
if(MSVC)
    # godot-cpp checks __cplusplus, which MSVC reports as 199711L unless told
    # to be truthful; the sources are UTF-8; no unwinding, as in the core.
    target_compile_options(weva_godot PRIVATE /Zc:__cplusplus /vmg /utf-8 /EHs-c- /W3)
    target_compile_definitions(weva_godot PRIVATE _HAS_EXCEPTIONS=0 NOMINMAX)
endif()

# The engine loads the library by the name in weva.gdextension, so the output
# goes straight into the demo project's addons directory.
# A DLL is a RUNTIME artifact to CMake, and a multi-config generator (Visual
# Studio) would otherwise add a Release/ or Debug/ level the .gdextension does
# not name; the per-config properties pin both to the one directory.
set(WEVA_GODOT_BIN "${CMAKE_CURRENT_SOURCE_DIR}/project/addons/weva/bin" CACHE PATH
    "Output directory for the Godot extension (can be isolated from an open editor)")
set_target_properties(weva_godot PROPERTIES
    OUTPUT_NAME "weva_godot"
    PREFIX ""
    LIBRARY_OUTPUT_DIRECTORY "${WEVA_GODOT_BIN}"
    RUNTIME_OUTPUT_DIRECTORY "${WEVA_GODOT_BIN}"
    LIBRARY_OUTPUT_DIRECTORY_RELEASE "${WEVA_GODOT_BIN}"
    RUNTIME_OUTPUT_DIRECTORY_RELEASE "${WEVA_GODOT_BIN}"
    LIBRARY_OUTPUT_DIRECTORY_DEBUG "${WEVA_GODOT_BIN}"
    RUNTIME_OUTPUT_DIRECTORY_DEBUG "${WEVA_GODOT_BIN}"
)
if(NOT WIN32)
    # The Linux/macOS artefacts keep their lib prefix (libweva_godot.so).
    set_target_properties(weva_godot PROPERTIES PREFIX "lib")
endif()

# A separate test extension exercises the actual font callback table against
# TextServer. It is never registered in or packaged with the product addon.
option(WEVA_GODOT_FONT_TESTS "Build the native font-adapter test extension" OFF)
if(WEVA_GODOT_FONT_TESTS)
    add_library(weva_font_tests SHARED src/godot_font.cpp tests/font_backend_tests.cpp)
    target_link_libraries(weva_font_tests PRIVATE weva godot-cpp weva_icu_common)
    if(MSVC)
        target_compile_options(weva_font_tests PRIVATE /Zc:__cplusplus /vmg /utf-8 /EHs-c- /W3)
        target_compile_definitions(weva_font_tests PRIVATE _HAS_EXCEPTIONS=0 NOMINMAX)
    endif()
    set_target_properties(weva_font_tests PROPERTIES
        PREFIX ""
        LIBRARY_OUTPUT_DIRECTORY "${WEVA_GODOT_BIN}"
        RUNTIME_OUTPUT_DIRECTORY "${WEVA_GODOT_BIN}"
        LIBRARY_OUTPUT_DIRECTORY_RELEASE "${WEVA_GODOT_BIN}"
        RUNTIME_OUTPUT_DIRECTORY_RELEASE "${WEVA_GODOT_BIN}"
        LIBRARY_OUTPUT_DIRECTORY_DEBUG "${WEVA_GODOT_BIN}"
        RUNTIME_OUTPUT_DIRECTORY_DEBUG "${WEVA_GODOT_BIN}")
    if(NOT WIN32)
        set_target_properties(weva_font_tests PROPERTIES PREFIX "lib")
    endif()
endif()
