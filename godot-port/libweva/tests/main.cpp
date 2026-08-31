#include "check.h"
void test_arena();
void test_intern();
void test_geometry();
void test_dom();
int main() {
    test_arena();
    test_intern();
    test_geometry();
    test_dom();
    std::printf("%d checks, %d failures\n", wevatest::checks, wevatest::failures);
    return wevatest::failures == 0 ? 0 : 1;
}
