# The Unity host's native plugin: libweva's C ABI as a shared library with
# nothing else exported. Unity loads it through P/Invoke; the C# side of the
# host (bindings, font and render adapters) lives in the Unity package.
#
#   cmake -S hosts/unity -B build-unity -G Ninja -DCMAKE_BUILD_TYPE=Release
#   cmake --build build-unity
#
# The export list is generated from weva_c.h by gen_exports.py, so the header
# stays the single source of truth for what a host may call; the same script
# feeds the P/Invoke generator. Statically linked CRT and ICU: the plugin
# carries no runtime dependency.

cmake_minimum_required(VERSION 3.20)
cmake_policy(SET CMP0091 NEW)
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded" CACHE STRING "Static CRT: the plugin carries no runtime dependency")
project(weva_unity CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_POSITION_INDEPENDENT_CODE ON)

set(WEVA_BUILD_TESTS OFF CACHE BOOL "" FORCE)
add_subdirectory(${CMAKE_CURRENT_SOURCE_DIR}/../.. ${CMAKE_CURRENT_BINARY_DIR}/weva-core)

# Hidden visibility only for the plugin's own sources, set after the core is
# added: a hidden symbol cannot be exported by a version script, and the
# core's C API must be. The version script (local: *) hides the rest.
set(CMAKE_CXX_VISIBILITY_PRESET hidden)
set(CMAKE_VISIBILITY_INLINES_HIDDEN ON)

find_package(Python3 3.9 REQUIRED COMPONENTS Interpreter)
set(WEVA_C_HEADER "${CMAKE_CURRENT_SOURCE_DIR}/../../libweva/include/weva_c.h")
set(WEVA_UNITY_HEADER "${CMAKE_CURRENT_SOURCE_DIR}/src/weva_unity.h")
set(WEVA_EXPORT_FORMAT version)
set(WEVA_EXPORT_FILE "${CMAKE_CURRENT_BINARY_DIR}/weva_core.map")
if(WIN32)
    set(WEVA_EXPORT_FORMAT def)
    set(WEVA_EXPORT_FILE "${CMAKE_CURRENT_BINARY_DIR}/weva_core.def")
endif()
add_custom_command(OUTPUT "${WEVA_EXPORT_FILE}"
    COMMAND "${Python3_EXECUTABLE}" "${CMAKE_CURRENT_SOURCE_DIR}/gen_exports.py"
        --header "${WEVA_C_HEADER}" --header "${WEVA_UNITY_HEADER}"
        --format ${WEVA_EXPORT_FORMAT} --out "${WEVA_EXPORT_FILE}" --library weva_core
    COMMAND "${Python3_EXECUTABLE}" "${CMAKE_CURRENT_SOURCE_DIR}/gen_exports.py"
        --header "${WEVA_C_HEADER}" --header "${WEVA_UNITY_HEADER}"
        --format json --out "${CMAKE_CURRENT_BINARY_DIR}/weva_core_api.json"
    DEPENDS "${CMAKE_CURRENT_SOURCE_DIR}/gen_exports.py" "${WEVA_C_HEADER}" "${WEVA_UNITY_HEADER}"
    COMMENT "Listing libweva's C API from weva_c.h and weva_unity.h")

# The plugin has almost no code of its own: the export file makes the linker
# pull the C API out of the static core. On Windows a .def does that; on GNU
# linkers the whole core archive is linked so the version script has every
# symbol to choose from.
# iOS takes plugins as static archives that Xcode links into the player:
# WEVA_UNITY_STATIC builds weva_core as one, and the core and ICU archives
# beside it are what the Unity project lists as plugins.
option(WEVA_UNITY_STATIC "Build weva_core as a static archive (iOS)" OFF)
if(WEVA_UNITY_STATIC)
    add_library(weva_core STATIC src/plugin.cpp)
    target_link_libraries(weva_core PUBLIC weva)
else()
    add_library(weva_core SHARED src/plugin.cpp "${WEVA_EXPORT_FILE}")
    if(WIN32)
        target_link_options(weva_core PRIVATE "/DEF:${WEVA_EXPORT_FILE}")
        target_link_libraries(weva_core PRIVATE weva)
    elseif(APPLE)
        target_link_libraries(weva_core PRIVATE -Wl,-force_load,$<TARGET_FILE:weva> weva)
    else()
        target_link_options(weva_core PRIVATE "-Wl,--version-script=${WEVA_EXPORT_FILE}")
        target_link_libraries(weva_core PRIVATE -Wl,--whole-archive weva -Wl,--no-whole-archive)
    endif()
endif()
target_include_directories(weva_core PRIVATE ${CMAKE_CURRENT_SOURCE_DIR}/../../libweva/include ${CMAKE_CURRENT_SOURCE_DIR}/src)
set_target_properties(weva_core PROPERTIES PREFIX "" OUTPUT_NAME "weva_core")
set(weva_output_targets weva_core)
if(WEVA_UNITY_STATIC)
    # Static target_link_libraries records a consumer link interface; it does
    # not merge dependency objects into weva_core.a. Ship all four archives.
    list(APPEND weva_output_targets weva weva_icu_i18n weva_icu_common)
    if(NOT DEFINED WEVA_UNITY_BIN)
        set(WEVA_UNITY_BIN "${CMAKE_CURRENT_BINARY_DIR}/bin")
    endif()
endif()
if(DEFINED WEVA_UNITY_BIN)
    set_target_properties(${weva_output_targets} PROPERTIES
        LIBRARY_OUTPUT_DIRECTORY "${WEVA_UNITY_BIN}" RUNTIME_OUTPUT_DIRECTORY "${WEVA_UNITY_BIN}"
        ARCHIVE_OUTPUT_DIRECTORY "${WEVA_UNITY_BIN}"
        LIBRARY_OUTPUT_DIRECTORY_RELEASE "${WEVA_UNITY_BIN}" RUNTIME_OUTPUT_DIRECTORY_RELEASE "${WEVA_UNITY_BIN}"
        ARCHIVE_OUTPUT_DIRECTORY_RELEASE "${WEVA_UNITY_BIN}")
endif()

# The same fingerprint the Godot packager verifies: library digest, source
# tree digest, git state and compiler, written beside the plugin.
set(weva_metadata_command "${Python3_EXECUTABLE}" "${CMAKE_CURRENT_SOURCE_DIR}/build_metadata.py"
        --library "$<TARGET_FILE:weva_core>"
        --source-dir "${CMAKE_CURRENT_SOURCE_DIR}/../.."
        --configuration "$<CONFIG>"
        --compiler "${CMAKE_CXX_COMPILER_ID}"
        --compiler-version "${CMAKE_CXX_COMPILER_VERSION}"
        --cmake-version "${CMAKE_VERSION}"
        --sanitizers "${WEVA_SANITIZERS}")
add_custom_command(TARGET weva_core POST_BUILD COMMAND ${weva_metadata_command} VERBATIM)
# Metadata helpers and Git state can change without relinking the library.
# Refresh on ordinary builds too, including Visual Studio where LINK_DEPENDS
# cannot make a Python helper trigger the post-link command.
add_custom_target(weva_core_metadata ALL COMMAND ${weva_metadata_command}
    DEPENDS weva_core VERBATIM)

# A host-free check that the plugin loads, reports its ABI and can run a
# document end to end through the exported entry points alone. Only where it
# can run: a cross-compiled plugin (Android, iOS) is verified on the device.
if(NOT CMAKE_CROSSCOMPILING AND NOT WEVA_UNITY_STATIC)
    add_executable(weva_core_load_test tests/load_test.cpp)
    target_include_directories(weva_core_load_test PRIVATE ${CMAKE_CURRENT_SOURCE_DIR}/../../libweva/include ${CMAKE_CURRENT_SOURCE_DIR}/src)
    if(NOT WIN32)
        target_link_libraries(weva_core_load_test PRIVATE ${CMAKE_DL_LIBS})
    endif()
    add_dependencies(weva_core_load_test weva_core)
endif()
