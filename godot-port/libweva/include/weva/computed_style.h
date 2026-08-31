#pragma once
#include "weva/css_properties.h"

#include <cstdint>
#include <map>
#include <string>
#include <string_view>
#include <vector>

// Ports Runtime/Css/Cascade/ComputedStyle.cs.
//
// Storage is an id-indexed array of raw value strings plus an occupancy
// bitset. The array indexing is the point: layout and paint read hot
// properties by cached id, so a lookup is an array load rather than a string
// hash. Custom properties (`--foo`) have no id and spill to a side map.
//
// The C# keeps BOTH a bool[] and a parallel ulong[] bitset — the bool[] for
// single-load hot readers, the bitset so FillInherited can iterate
// `(parent & ~child & inheritedMask)` in O(words) instead of scanning every
// registered id. Both are kept here for the same reason.
//
// Not ported: the per-slot parsed-CssValue cache. It is a memo, not
// semantics, and it interacts with the arena lifetime rules — revisit once
// the cascade is running and there is something to measure.

namespace weva {

class ComputedStyle {
public:
    ComputedStyle() = default;

    // Raw string access by id. Returns the empty string when unset; use
    // contains() to distinguish "unset" from "set to empty".
    std::string_view get(int property_id) const;
    bool try_get(int property_id, std::string* out) const;
    bool contains(int property_id) const;
    void set(int property_id, std::string_view value);

    // Name-keyed access, routing custom properties to the side map.
    std::string_view get(std::string_view property) const;
    void set(std::string_view property, std::string_view value);
    bool contains(std::string_view property) const;

    // !important tracking, consulted when a later declaration tries to
    // overwrite an earlier important one.
    bool is_important(int property_id) const;
    void set_important(int property_id, bool important);

    void clear();
    int set_count() const { return set_count_; }
    int64_t version() const { return version_; }

    const std::vector<uint64_t>& occupied_bits() const { return occupied_bits_; }
    const std::map<std::string, std::string>& custom_properties() const { return custom_; }

    // Ids currently set, ascending. Allocates; for tests and diagnostics.
    std::vector<int> set_ids() const;

private:
    void ensure_capacity(int id);

    std::vector<std::string> values_;
    std::vector<bool> occupied_;
    std::vector<uint64_t> occupied_bits_;
    std::vector<bool> important_;
    std::map<std::string, std::string> custom_;
    int set_count_ = 0;
    int64_t version_ = 0;
};

} // namespace weva
