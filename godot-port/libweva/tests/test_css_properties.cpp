#include "check.h"
#include "weva/css_properties.h"

#include <string>
#include <vector>

using namespace weva;

// The property registry's name index.
//
// id_of is the hottest function in a layout pass -- every `get(style,
// "border-top-width")` goes through it, and layout does little else. It was a
// binary search, then a hash over every byte of the name, and is now a hash
// over the length and four sampled bytes. Correctness never depended on the
// hash: a hit is confirmed by comparing the whole name, and a collision only
// costs another probe. Its SPREAD does, so it is checked here rather than left
// to show up as an unexplained slowdown.
void test_css_property_index() {
    const CssPropertyRegistry& reg = CssPropertyRegistry::instance();
    CHECK(reg.count() > 100);

    // Every registered property resolves to its own id, and back to its name.
    for (int id = 0; id < static_cast<int>(reg.count()); ++id) {
        const std::string_view name = reg.name_of(id);
        CHECK(!name.empty());
        CHECK(reg.id_of(name) == id);
    }

    // Names that are not properties are custom, however close they sit to one.
    for (const char* absent : {"", "-", "border-top-widthx", "orderr-top-width",
                               "border-top-widt", "--custom", "xyzzy"}) {
        CHECK(reg.id_of(absent) == kCustomPropertyId);
    }

    // Long shared prefixes are the case the sampled hash has to survive:
    // sixteen of these differ only in bytes it never reads directly.
    const char* siblings[] = {"border-top-width",  "border-top-style",  "border-top-color",
                              "border-left-width", "border-left-style", "border-left-color",
                              "border-right-width", "border-right-style", "border-right-color",
                              "border-bottom-width", "border-bottom-style", "border-bottom-color",
                              "margin-top", "margin-left", "margin-right", "margin-bottom",
                              "padding-top", "padding-left", "padding-right", "padding-bottom"};
    for (const char* name : siblings) {
        const int id = reg.id_of(name);
        CHECK(id != kCustomPropertyId);
        CHECK(reg.name_of(id) == std::string_view(name));
    }

    // A chain this short is what makes the lookup one probe in practice. The
    // bound is generous; the point is that a hash change that clusters the
    // table fails here instead of quietly costing 30% of a layout pass.
    CHECK(reg.max_probe() <= 6);
}
