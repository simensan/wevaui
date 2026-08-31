#include "check.h"
void test_arena();
void test_intern();
int main() {
    test_arena();
    test_intern();
    std::printf("%d checks, %d failures\n", wevatest::checks, wevatest::failures);
    return wevatest::failures == 0 ? 0 : 1;
}
