add_executable(weva_bench main.cpp)
target_link_libraries(weva_bench PRIVATE weva)
# -rdynamic so backtrace_symbols can name static functions in --profile mode;
# -fno-omit-frame-pointer so the walk is not truncated by the optimiser.
target_link_options(weva_bench PRIVATE -rdynamic)
target_compile_options(weva_bench PRIVATE -fno-omit-frame-pointer)
