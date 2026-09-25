# ICU's C API provides Unicode collation search. Pin sources and embedded data
# so a project does not silently inherit a different system ICU/browser profile.
include(FetchContent)
if(POLICY CMP0135)
    cmake_policy(SET CMP0135 NEW)
endif()
FetchContent_Declare(weva_icu_source
    URL https://github.com/unicode-org/icu/releases/download/release-78.3/icu4c-78.3-sources.tgz
    URL_HASH SHA256=3a2e7a47604ba702f345878308e6fefeca612ee895cf4a5f222e7955fabfe0c0
)
FetchContent_MakeAvailable(weva_icu_source)
set(icu_source "${weva_icu_source_SOURCE_DIR}/source")
find_package(Python3 3.9 REQUIRED COMPONENTS Interpreter)
set(icu_data "${CMAKE_CURRENT_BINARY_DIR}/weva_icu_data.cpp")
add_custom_command(OUTPUT "${icu_data}"
    COMMAND "${Python3_EXECUTABLE}" "${CMAKE_CURRENT_SOURCE_DIR}/filter_data.py"
            "${icu_source}/data/in/icudt78l.dat" "${icu_data}"
    DEPENDS "${CMAKE_CURRENT_SOURCE_DIR}/filter_data.py" "${icu_source}/data/in/icudt78l.dat"
    VERBATIM)

foreach(part common i18n)
    # Use the upstream build manifest, including sources added by maintenance
    # releases. Feature guards remove formatting, conversion and transliteration.
    file(STRINGS "${icu_source}/${part}/sources.txt" sources REGEX "^[A-Za-z0-9_].*\\.cpp$")
    list(TRANSFORM sources PREPEND "${icu_source}/${part}/")
    add_library(weva_icu_${part} STATIC ${sources})
    target_link_libraries(weva_icu_${part} PRIVATE weva_sanitizers)
    # SYSTEM so ICU's headers do not hand their warnings to everything that
    # includes them. Upstream code we do not patch.
    target_include_directories(weva_icu_${part} SYSTEM PUBLIC "${icu_source}/common" "${icu_source}/i18n")
    target_compile_definitions(weva_icu_${part} PUBLIC
        U_STATIC_IMPLEMENTATION U_HAVE_LIB_SUFFIX=1 U_LIB_SUFFIX_C_NAME=_weva
        UCONFIG_NO_CONVERSION=1 UCONFIG_NO_LEGACY_CONVERSION=1
        UCONFIG_NO_TRANSLITERATION=1 UCONFIG_NO_REGULAR_EXPRESSIONS=1
        UCONFIG_NO_FORMATTING=1 UCONFIG_NO_FILE_IO=1 U_ENABLE_DYLOAD=0)
    set_target_properties(weva_icu_${part} PROPERTIES POSITION_INDEPENDENT_CODE ON)
    if(MSVC)
        target_compile_options(weva_icu_${part} PRIVATE /utf-8 /Zc:__cplusplus /EHsc)
        target_compile_definitions(weva_icu_${part} PRIVATE _CRT_SECURE_NO_WARNINGS NOMINMAX)
    else()
        # 12 of the 22 warnings in a clean gcc build came from these two
        # targets. They are vendored upstream sources, fetched and unpatched,
        # so the warnings are not ours to fix and they bury the ten that are.
        target_compile_options(weva_icu_${part} PRIVATE -w)
    endif()
endforeach()
target_compile_definitions(weva_icu_common PRIVATE U_COMMON_IMPLEMENTATION)
target_compile_definitions(weva_icu_i18n PRIVATE U_I18N_IMPLEMENTATION)
target_sources(weva_icu_common PRIVATE "${icu_data}")
target_link_libraries(weva_icu_i18n PUBLIC weva_icu_common)
if(WIN32)
    target_link_libraries(weva_icu_common PRIVATE advapi32)
elseif(UNIX)
    find_package(Threads REQUIRED)
    target_link_libraries(weva_icu_common PRIVATE Threads::Threads ${CMAKE_DL_LIBS})
endif()
