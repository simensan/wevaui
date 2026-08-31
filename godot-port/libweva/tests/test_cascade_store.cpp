#include "check.h"
#include "weva/computed_style.h"
#include "weva/css_properties.h"
#include <string>

using namespace weva;

void test_property_registry() {
    auto& reg = CssPropertyRegistry::instance();

    // ---- the table came across whole
    CHECK(reg.count() == 334);

    // ---- ids are assigned in REGISTRATION order, and hot paths cache them
    CHECK(reg.id_of("display") == 0);
    CHECK(reg.id_of("position") == 1);
    CHECK(reg.id_of("top") == 2);

    // ---- inheritance flags: the cascade's whole inherit step keys on these
    CHECK(!reg.is_inherited(reg.id_of("display")));
    CHECK(!reg.is_inherited(reg.id_of("width")));
    CHECK(reg.is_inherited(reg.id_of("color")));
    CHECK(reg.is_inherited(reg.id_of("font-size")));
    CHECK(reg.is_inherited(reg.id_of("font-family")));
    CHECK(reg.is_inherited(reg.id_of("line-height")));
    CHECK(reg.is_inherited(reg.id_of("visibility")));
    CHECK(!reg.is_inherited(reg.id_of("margin-top")));

    // ---- initial values
    CHECK(reg.initial_value(reg.id_of("display")) == "inline");
    CHECK(reg.initial_value(reg.id_of("position")) == "static");
    CHECK(reg.initial_value(reg.id_of("top")) == "auto");

    // ---- unknown and custom properties have no id
    CHECK(reg.id_of("not-a-property") == kCustomPropertyId);
    CHECK(reg.id_of("--my-var") == kCustomPropertyId);
    CHECK(CssPropertyRegistry::is_custom_property("--x"));
    CHECK(!CssPropertyRegistry::is_custom_property("-x"));
    CHECK(!CssPropertyRegistry::is_custom_property("color"));
    CHECK(!CssPropertyRegistry::is_custom_property("--"));

    // ---- re-registration keeps the id, so cached ids stay valid.
    // @property can redefine a registered custom property while the document
    // is live; if that moved the id, every cached id would silently repoint.
    int before = reg.id_of("display");
    int again = reg.register_property("display", false, "block");
    CHECK(again == before);
    CHECK(reg.initial_value(before) == "block");
    reg.register_property("display", false, "inline");   // restore
    CHECK(reg.initial_value(before) == "inline");

    // ---- a genuinely new property appends
    int n = reg.count();
    int fresh = reg.register_property("--weva-test-prop", true, "0");
    CHECK(fresh == n);
    CHECK(reg.count() == n + 1);
    CHECK(reg.is_inherited(fresh));
    CHECK(reg.name_of(fresh) == "--weva-test-prop");

    // ---- out-of-range ids are handled, not indexed
    CHECK(reg.by_id(-1) == nullptr);
    CHECK(reg.by_id(999999) == nullptr);
    CHECK(reg.name_of(999999).empty());
}

void test_computed_style() {
    auto& reg = CssPropertyRegistry::instance();
    const int display = reg.id_of("display");
    const int color = reg.id_of("color");
    const int width = reg.id_of("width");

    ComputedStyle s;
    CHECK(s.set_count() == 0);
    CHECK(!s.contains(display));
    CHECK(s.get(display).empty());

    s.set(display, "flex");
    CHECK(s.contains(display));
    CHECK(s.get(display) == "flex");
    CHECK(s.set_count() == 1);

    // ---- "set to empty" is distinguishable from "never set"
    s.set(color, "");
    CHECK(s.contains(color));
    CHECK(s.get(color).empty());
    CHECK(s.set_count() == 2);
    CHECK(!s.contains(width));

    // ---- a no-op write must NOT bump the version. The invalidation
    // architecture keys caches on version numbers, so a spurious bump
    // re-cascades everything downstream — this is the 0.08ms vs 8.3ms hinge.
    int64_t v = s.version();
    s.set(display, "flex");
    CHECK(s.version() == v);
    s.set(display, "block");
    CHECK(s.version() != v);
    CHECK(s.set_count() == 2);   // overwriting is not a new slot

    // ---- the occupancy bitset mirrors the bool vector
    {
        ComputedStyle b;
        b.set(0, "a");
        b.set(65, "b");     // second word
        b.set(130, "c");    // third word
        const auto& bits = b.occupied_bits();
        CHECK(bits.size() >= 3);
        CHECK((bits[0] & 1ULL) != 0);
        CHECK((bits[1] & (1ULL << 1)) != 0);
        CHECK((bits[2] & (1ULL << 2)) != 0);
        CHECK((bits[0] & 2ULL) == 0);
        auto ids = b.set_ids();
        CHECK(ids.size() == 3 && ids[0] == 0 && ids[1] == 65 && ids[2] == 130);
    }

    // ---- important flags
    CHECK(!s.is_important(display));
    s.set_important(display, true);
    CHECK(s.is_important(display));
    CHECK(!s.is_important(color));

    // ---- custom properties route to the side map, not the indexed array
    s.set("--brand", "#f00");
    CHECK(s.contains("--brand"));
    CHECK(s.get("--brand") == "#f00");
    CHECK(s.custom_properties().size() == 1);
    CHECK(s.set_count() == 2);          // unchanged: no id was consumed
    int64_t cv = s.version();
    s.set("--brand", "#f00");           // no-op here too
    CHECK(s.version() == cv);

    // ---- name-keyed access agrees with id-keyed access
    s.set("width", "10px");
    CHECK(s.get(width) == "10px");
    CHECK(s.get("width") == "10px");
    CHECK(s.contains("width"));
    CHECK(!s.contains("--nope"));

    // ---- an unknown non-custom name is treated as custom, not dropped
    s.set("totally-unknown", "1");
    CHECK(s.get("totally-unknown") == "1");

    s.clear();
    CHECK(s.set_count() == 0);
    CHECK(!s.contains(display));
    CHECK(s.custom_properties().empty());
}
