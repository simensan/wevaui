if(NOT DEFINED PROBE OR NOT DEFINED MODE OR NOT DEFINED WORK)
    message(FATAL_ERROR "PROBE, MODE and WORK are required")
endif()
file(MAKE_DIRECTORY "${WORK}")
# This deliberate failure needs the sanitizer diagnostic and a nonzero exit,
# not symbol names. Avoid an external symbolizer stalling the positive control
# after ASan has already detected the overflow. Actual test reports keep their
# normal symbolization settings.
set(probe_command "${PROBE}" "${MODE}")
if(MODE STREQUAL "address")
    set(probe_command "${CMAKE_COMMAND}" -E env
        "ASAN_OPTIONS=$ENV{ASAN_OPTIONS}:symbolize=0" "${PROBE}" "${MODE}")
endif()
execute_process(COMMAND ${probe_command}
    RESULT_VARIABLE result OUTPUT_VARIABLE output ERROR_VARIABLE error TIMEOUT 30)
file(WRITE "${WORK}/${MODE}.log" "${output}${error}\nExit: ${result}\n")
if(MODE STREQUAL "address")
    set(expected "ERROR: AddressSanitizer: heap-buffer-overflow")
elseif(MODE STREQUAL "undefined")
    set(expected "runtime error: signed integer overflow")
else()
    message(FATAL_ERROR "Unknown sanitizer probe: ${MODE}")
endif()
if(NOT "${result}" MATCHES "^-?[0-9]+$" OR result STREQUAL "0" OR
        NOT "${output}${error}" MATCHES "${expected}")
    message(FATAL_ERROR "${MODE} instrumentation did not reject the positive control; see ${WORK}/${MODE}.log")
endif()
message(STATUS "${MODE} instrumentation detected the deliberate error")
