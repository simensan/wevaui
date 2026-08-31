add_executable(weva_tests test_arena.cpp test_intern.cpp main.cpp)
target_link_libraries(weva_tests PRIVATE weva)
add_test(NAME weva_tests COMMAND weva_tests)
