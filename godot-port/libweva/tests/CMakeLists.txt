add_executable(weva_tests
    main.cpp
    test_arena.cpp
    test_intern.cpp
    test_geometry.cpp
    test_dom.cpp
    test_html.cpp
    test_html_parser.cpp
)
target_link_libraries(weva_tests PRIVATE weva)
add_test(NAME weva_tests COMMAND weva_tests)
