add_library(weva STATIC
    src/arena.cpp
    src/intern.cpp
    src/geometry.cpp
    src/color.cpp
    src/dom.cpp
    src/html_tables.cpp
    src/html_tokenizer.cpp
    src/html_parser.cpp
)
target_include_directories(weva PUBLIC include)
target_link_libraries(weva PUBLIC weva_flags)
