add_executable(weva_bench main.cpp)
target_link_libraries(weva_bench PRIVATE weva)
# -rdynamic so backtrace_symbols can name static functions in --profile mode;
# -fno-omit-frame-pointer so the walk is not truncated by the optimiser.
if(WIN32)
    target_link_libraries(weva_bench PRIVATE dbghelp)
else()
    target_link_options(weva_bench PRIVATE -rdynamic)
    target_compile_options(weva_bench PRIVATE -fno-omit-frame-pointer)
endif()

add_test(NAME weva_bench_cli COMMAND ${CMAKE_COMMAND}
    -DBENCH=$<TARGET_FILE:weva_bench> -DWORK=${CMAKE_CURRENT_BINARY_DIR}/cli-test
    -DPLATFORM_WINDOWS=${WIN32} -P ${CMAKE_CURRENT_SOURCE_DIR}/check_cli.cmake)
