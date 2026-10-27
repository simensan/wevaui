cmake_minimum_required(VERSION 3.20)
project(weva_native_sdk LANGUAGES CXX)
set(CMAKE_POSITION_INDEPENDENT_CODE ON)
# Rust's normal MSVC builds use the dynamic CRT. Keep all three archives on it.
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreadedDLL")
set(WEVA_BUILD_TESTS OFF CACHE BOOL "Build the complete core test suite")
enable_testing()
add_subdirectory(../.. core)
add_executable(weva_native_smoke smoke.cpp)
target_link_libraries(weva_native_smoke PRIVATE weva)
add_test(NAME weva_native_smoke COMMAND weva_native_smoke)
file(GENERATE OUTPUT "${CMAKE_BINARY_DIR}/native-$<CONFIG>.json" CONTENT
"{\n  \"configuration\": \"$<CONFIG>\",\n  \"system\": \"${CMAKE_SYSTEM_NAME}\",\n  \"processor\": \"${CMAKE_SYSTEM_PROCESSOR}\",\n  \"osx_architectures\": \"${CMAKE_OSX_ARCHITECTURES}\",\n  \"compiler_architecture\": \"${CMAKE_CXX_COMPILER_ARCHITECTURE_ID}\",\n  \"pointer_size\": ${CMAKE_SIZEOF_VOID_P},\n  \"compiler\": \"${CMAKE_CXX_COMPILER_ID} ${CMAKE_CXX_COMPILER_VERSION}\",\n  \"cmake\": \"${CMAKE_VERSION}\",\n  \"libraries\": {\n    \"weva\": \"$<TARGET_FILE:weva>\",\n    \"weva_icu_i18n\": \"$<TARGET_FILE:weva_icu_i18n>\",\n    \"weva_icu_common\": \"$<TARGET_FILE:weva_icu_common>\"\n  }\n}\n")
