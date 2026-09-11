#include "check.h"
#include "weva/intern.h"
#include <string>

void test_intern() {
    weva::SymbolTable t;

    weva::Symbol a = t.intern("margin-left");
    weva::Symbol b = t.intern("margin-left");
    CHECK(a == b);
    CHECK(a != weva::kInvalidSymbol);
    CHECK(t.text(a) == "margin-left");

    CHECK(t.find("never-seen") == weva::kInvalidSymbol);
    CHECK(t.find("margin-left") == a);

    // Interning must survive the caller's buffer dying — the table copies.
    weva::Symbol c;
    {
        std::string temporary = "display";
        c = t.intern(std::string_view(temporary));
    }
    CHECK(t.text(c) == "display");

    // Regression: storage_ was a std::vector<std::string>, whose reallocation
    // moves SSO character data and dangles every string_view key in the index.
    // Intern enough short symbols to force several reallocations, then confirm
    // the earliest entries still resolve and still de-duplicate.
    for (int i = 0; i < 2000; ++i) t.intern("s" + std::to_string(i));
    CHECK(t.text(a) == "margin-left");
    CHECK(t.intern("margin-left") == a);
    CHECK(t.find("s0") != weva::kInvalidSymbol);
    CHECK(t.text(t.find("s1999")) == "s1999");
}
