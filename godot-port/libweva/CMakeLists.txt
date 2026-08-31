add_library(weva STATIC
    src/arena.cpp
    src/intern.cpp
)
target_include_directories(weva PUBLIC include)
target_link_libraries(weva PUBLIC weva_flags)
