#include "check.h"
void test_arena();
void test_intern();
void test_geometry();
void test_dom();
void test_html();
void test_html_parser();
void test_css_tokenizer();
int main() {
    test_arena();
    test_intern();
    test_geometry();
    test_dom();
    test_html();
    test_html_parser();
    test_css_tokenizer();
    std::printf("%d checks, %d failures\n", wevatest::checks, wevatest::failures);
    return wevatest::failures == 0 ? 0 : 1;
}
