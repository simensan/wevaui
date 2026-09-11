#include "check.h"
#include "weva/computed_style.h"
#include "weva/css_properties.h"
#include <string>
#include <utility>

using namespace weva;

void test_property_registry() {
    auto& reg = CssPropertyRegistry::instance();

    // ---- the table came across whole
    CHECK(reg.count() == 334 + 13);   // the generated table plus the scroll-snap appendix

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

    // ---- re-registering must refresh the side tables, not just the record.
    // is_inherited() and initial_value() answer from arrays built alongside
    // properties_, and the re-registration path wrote the record and returned
    // -- so `@property { inherits: false }` redefining an already-registered
    // custom property left the inheritance flag at whatever it was FIRST
    // registered with, and every descendant went on inheriting a property
    // declared not to.
    CHECK(reg.register_property("--weva-test-prop", false, "1") == fresh);
    CHECK(!reg.is_inherited(fresh));
    CHECK(reg.initial_value(fresh) == "1");
    CHECK(reg.register_property("--weva-test-prop", true, "2") == fresh);
    CHECK(reg.is_inherited(fresh));
    CHECK(reg.initial_value(fresh) == "2");

    // A long value, so the assignment has to reallocate the record's string:
    // the cached view has to be re-pointed at the new buffer, not left on the
    // freed one.
    const std::string long_initial(200, 'z');
    reg.register_property("--weva-test-prop", true, long_initial);
    CHECK(reg.initial_value(fresh) == long_initial);
    CHECK(reg.initial_value(fresh).size() == 200);

    // And the same for a built-in, whose inheritance the engine reads on
    // every miss of that property on every box.
    const int ls = reg.id_of("letter-spacing");
    CHECK(reg.is_inherited(ls));
    reg.register_property("letter-spacing", false, "normal");
    CHECK(!reg.is_inherited(ls));
    reg.register_property("letter-spacing", true, "normal");   // restore
    CHECK(reg.is_inherited(ls));

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
    // get() resolves through inherit-then-initial, so an unset slot yields the
    // registered initial rather than the empty string. contains() is the way to
    // ask whether THIS style set it directly.
    CHECK(s.get(display) == "inline");
    CHECK(s.get(reg.id_of("position")) == "static");

    s.set(display, "flex");
    CHECK(s.contains(display));
    CHECK(s.get(display) == "flex");
    CHECK(s.set_count() == 1);

    // ---- "set to empty" is distinguishable from "never set"
    s.set(color, "");
    CHECK(s.contains(color));
    CHECK(s.get(color).empty());          // explicitly empty, not the initial
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

    // Adding other declarations must preserve views and parsed values already
    // handed to callers, including small strings stored inside their owner.
    // Use reverse property order, then grow beyond the initial metadata size.
    {
        ComputedStyle values;
        values.set(width, "12px");
        const auto short_view = values.get(width);
        const auto* short_data = short_view.data();
        const auto* parsed_width = values.parsed(width);
        CHECK(parsed_width && parsed_width->raw == "12px");
        const std::string long_value(200, 'q');
        values.set(color, long_value);
        const auto long_view = values.get(color);
        const auto* long_data = long_view.data();
        for (int id = reg.count() - 1; id >= 0; --id) {
            if (id != width && id != color) values.set(id, std::to_string(id));
        }
        const int high_id = reg.count() + 129;
        values.set_important(high_id, true);  // metadata without a raw value
        CHECK(!values.contains(high_id));
        CHECK(values.is_important(high_id));
        values.set(high_id, "high");
        CHECK(short_view == "12px" && values.get(width).data() == short_data);
        CHECK(long_view == long_value && values.get(color).data() == long_data);
        CHECK(values.parsed(width) == parsed_width);
        CHECK(values.set_count() == reg.count() + 1);
        const auto ids = values.set_ids();
        CHECK(ids.size() == static_cast<size_t>(reg.count() + 1));
        CHECK(ids.front() == 0 && ids.back() == high_id);
        for (int id = 0; id < reg.count(); ++id) {
            CHECK(ids[static_cast<size_t>(id)] == id);
            const auto expected = id == width ? "12px" :
                id == color ? long_value : std::to_string(id);
            CHECK(values.get(id) == expected);
        }

        const auto version = values.version();
        values.set(width, short_view);
        CHECK(values.version() == version);
        values.unset(width);
        CHECK(values.get(width) == reg.initial_value(width));
        CHECK(!values.contains(width));
        values.set(width, "27px");
        CHECK(values.parsed(width) && values.parsed(width)->raw == "27px");
        std::string out;
        CHECK(values.try_get(high_id, &out) && out == "high");

        // Move/swap ownership, then clear and refill in a different order.
        // The cascade uses these operations when replacing retained styles.
        ComputedStyle moved = std::move(values);
        values.clear();
        values.set(display, "grid");
        CHECK(values.get(display) == "grid" && values.set_count() == 1);
        CHECK(moved.get(color).data() == long_data);
        std::swap(values, moved);
        CHECK(values.get(high_id) == "high" && values.is_important(high_id));
        CHECK(moved.get(display) == "grid");

        values.set("--page-reset", "old");
        values.set_inherit_parent(&moved);
        values.clear();
        CHECK(values.set_count() == 0 && values.set_ids().empty());
        CHECK(values.inherit_parent() == nullptr);
        CHECK(values.custom_properties().empty());
        CHECK(!values.is_important(high_id) && !values.contains(high_id));
        CHECK(values.get(width) == reg.initial_value(width));
        values.set(high_id, "reused");
        values.set(color, "");
        values.set(width, "39px");
        CHECK(values.set_count() == 3);
        CHECK(values.get(high_id) == "reused");
        CHECK(values.contains(color) && values.get(color).empty());
        CHECK(values.parsed(width) && values.parsed(width)->raw == "39px");

        // Diff by property id, independently of value insertion order.
        ComputedStyle equal;
        equal.set(width, "39px");
        equal.set(color, "");
        equal.set(high_id, "reused");
        std::vector<int> changed;
        bool unattributed = false;
        CHECK(!values.differs_from(equal, &changed, &unattributed));
        CHECK(changed.empty() && !unattributed);
        equal.set(high_id, "changed");
        CHECK(values.differs_from(equal, &changed, &unattributed));
        CHECK(changed.size() == 1 && changed[0] == high_id && !unattributed);
        values.unset(high_id);
        CHECK(values.set_count() == 2 && !values.is_important(high_id));
        values.clear();
        CHECK(values.get(width) == reg.initial_value(width));
    }
}

void test_lazy_inheritance() {
    auto& reg = CssPropertyRegistry::instance();
    const int color = reg.id_of("color");
    const int width = reg.id_of("width");

    ComputedStyle root, mid, leaf;
    root.set(color, "red");
    mid.set_inherit_parent(&root);
    leaf.set_inherit_parent(&mid);

    // ---- an inherited property resolves up the chain WITHOUT being copied
    CHECK(leaf.get(color) == "red");
    CHECK(!leaf.contains(color));         // nothing was materialised here
    CHECK(!mid.contains(color));
    CHECK(leaf.set_count() == 0);

    // ---- a non-inherited property falls to its initial, not the ancestor's
    root.set(width, "100px");
    CHECK(root.get(width) == "100px");
    CHECK(leaf.get(width) == "auto");

    // ---- an explicit value shadows the inherited one
    mid.set(color, "blue");
    CHECK(leaf.get(color) == "blue");
    CHECK(mid.get(color) == "blue");
    CHECK(root.get(color) == "red");

    // ---- unset() restores the fall-through
    mid.unset(color);
    CHECK(!mid.contains(color));
    CHECK(leaf.get(color) == "red");
    CHECK(mid.set_count() == 0);

    // ---- custom properties inherit through the chain too
    root.set("--brand", "#f00");
    CHECK(leaf.get("--brand") == "#f00");
    CHECK(leaf.contains("--brand"));      // reachable, though not local
    CHECK(leaf.custom_properties().empty());

    // Name views need not be terminated at their boundary. Custom names are
    // case-sensitive, and an explicitly empty value still shadows ancestors.
    const std::string name = "--component-primary-accent-color";
    const std::string extended = name + "-suffix";
    const std::string_view name_view(extended.data(), name.size());
    root.set(name, "first");
    root.set("--component-primary-Accent-color", "different case");
    CHECK(leaf.get(name_view) == "first");
    CHECK(leaf.get("--component-primary-Accent-color") == "different case");
    CHECK(leaf.get(extended).empty());
    CHECK(!leaf.contains(extended));
    CHECK(leaf.contains(name_view));
    CHECK(!leaf.contains_own(name_view));
    CHECK(root.contains_own(name_view));
    const int64_t before_read = leaf.version();
    CHECK(leaf.get(name_view) == "first");
    CHECK(leaf.version() == before_read);
    root.set(name, "updated");
    CHECK(leaf.get(name_view) == "updated");
    mid.set(name, "");
    CHECK(leaf.get(name_view).empty());
    CHECK(leaf.contains(name_view));
    CHECK(mid.contains_own(name_view));
    CHECK(root.get(name_view) == "updated");
    mid.clear();
    mid.set_inherit_parent(&root);
    CHECK(leaf.get(name_view) == "updated");
    ComputedStyle other;
    other.set(name, "other parent");
    leaf.set_inherit_parent(&other);
    CHECK(leaf.get(name_view) == "other parent");
    CHECK(!leaf.contains("--brand"));

    // ---- clear() drops the link, so reads fall back to initials only
    leaf.clear();
    CHECK(leaf.inherit_parent() == nullptr);
    CHECK(leaf.get(color) == "black");     // color's registered initial
}
