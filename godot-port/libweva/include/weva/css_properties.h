#pragma once
#include <cstdint>
#include <string>
#include <string_view>
#include <vector>

// Ports Runtime/Css/Cascade/{CssProperty,CssProperties}.cs.
//
// The registry maps a property name to a stable integer id, its inheritance
// flag, and its initial value. Ids are assigned in registration order and
// never move, so hot paths cache them once and index arrays directly instead
// of hashing a string per lookup — the C# comment notes this costs ~225K
// string hashes per cold cascade pass otherwise.
//
// Custom properties (`--foo`) get id kCustomPropertyId (-1) and spill to a
// side map rather than the indexed array, exactly as in the C#.

namespace weva {

constexpr int kCustomPropertyId = -1;

struct CssProperty {
    std::string name;
    bool is_inherited = false;
    std::string initial_value;
    int id = kCustomPropertyId;
};

class CssPropertyRegistry {
public:
    // The process-wide registry, pre-populated with the 334 built-in
    // properties in the C#'s registration order.
    static CssPropertyRegistry& instance();

    // Returns the id, or kCustomPropertyId for an unknown or custom property.
    int id_of(std::string_view name) const;
    // Registers (or re-registers) a property. Re-registration keeps the
    // existing id so cached ids stay valid — @property can redefine a
    // registered custom property's initial value at runtime.
    int register_property(std::string_view name, bool inherited, std::string_view initial);

    const CssProperty* by_id(int id) const;
    const CssProperty* by_name(std::string_view name) const;
    std::string_view name_of(int id) const;
    bool is_inherited(int id) const;
    std::string_view initial_value(int id) const;
    int count() const { return static_cast<int>(properties_.size()); }

    static bool is_custom_property(std::string_view name) {
        return name.size() > 2 && name[0] == '-' && name[1] == '-';
    }

private:
    CssPropertyRegistry();
    std::vector<CssProperty> properties_;                 // indexed by id
    std::vector<std::pair<std::string, int>> sorted_;      // name -> id, sorted; keeps order
    // Open-addressed name -> id index. id_of ran a binary search over
    // `sorted_`, so every `get(style, "border-top-width")` cost about eight
    // string comparisons -- and layout does nothing but that. Sampling a
    // layout pass of vendor.html put roughly a third of it inside id_of, more
    // than any other function by a wide margin. A hash lookup is one probe and
    // one comparison.
    //
    // Power-of-two sized and kept under half full, so a probe chain is short;
    // the table is rebuilt whenever a property is registered.
    std::vector<int> hash_slots_;   // -1 when empty, else an id
    // Indexed by id. is_inherited() is asked on every property read that falls
    // through to the inherit chain, and reaching it through by_id() is a bounds
    // check and a pointer chase to fetch one bool.
    std::vector<bool> inherited_;
    size_t hash_mask_ = 0;
    static size_t hash_name(std::string_view name);

public:
    // Diagnostic: the longest probe chain the name index holds.
    int max_probe() const;

private:
    void rebuild_index();
};

} // namespace weva
