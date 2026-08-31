#include "check.h"
void test_arena();
void test_intern();
void test_geometry();
void test_dom();
void test_html();
void test_html_parser();
void test_css_tokenizer();
void test_css_parser();
void test_css_value();
void test_css_color();
void test_css_calc();
void test_selector();
void test_selector_has();
void test_selector_match();
int main() {
    test_arena();
    test_intern();
    test_geometry();
    test_dom();
    test_html();
    test_html_parser();
    test_css_tokenizer();
    test_css_parser();
    test_css_value();
    test_css_color();
    test_css_calc();
    test_selector();
    test_selector_has();
    test_selector_match();
    std::printf("%d checks, %d failures\n", wevatest::checks, wevatest::failures);
    return wevatest::failures == 0 ? 0 : 1;
}
