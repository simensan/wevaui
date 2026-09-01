#pragma once
#include "weva/css_properties.h"
#include "weva/css_value.h"

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
// The per-slot parsed-CssValue cache IS ported, after the measurement this
// comment used to wait for. `tools/weva_bench` put 97% of a layout pass's
// heap allocations — 10.9 MB of 11.1 on a 1691-box document — in
// parse_css_value, because resolve_length re-parsed the raw declaration text
// on every read, several times per box wherever shrink-to-fit lays a box out
// repeatedly. The cache is a memo and changes no semantics: the same string
// parses to the same value.

namespace weva {

class ComputedStyle {
public:
    ComputedStyle() = default;

    // Inheritance and initial values are resolved LAZILY on read rather than
    // materialised per element.
    //
    // Writing all 334 registered initial values into every element's style
    // measured at 342 ms for 1004 elements — ~335,000 string assignments per
    // pass — which was the entire cascade runtime. Since the initial-value
    // table is immutable and shared, an unset slot can simply defer to it, and
    // an unset INHERITED slot can defer to the parent. Nothing is copied.
    //
    // Lifetime: `parent` must outlive this style. In a tree walk the parent's
    // style lives in the caller's frame above the child's, which satisfies
    // that naturally — but a style that outlives its walk must not keep the
    // pointer.
    void set_inherit_parent(const ComputedStyle* parent) { parent_ = parent; }
    const ComputedStyle* inherit_parent() const { return parent_; }

    // Raw string access by id. Resolves through the inherit chain and then the
    // registry's initial value, so an unset slot still yields the correct
    // computed value. Use contains() to ask whether THIS style set it directly.
    std::string_view get(int property_id) const;
    bool try_get(int property_id, std::string* out) const;
    bool contains(int property_id) const;
    void set(int property_id, std::string_view value);

    // Name-keyed access, routing custom properties to the side map.
    std::string_view get(std::string_view property) const;
    void set(std::string_view property, std::string_view value);
    bool contains(std::string_view property) const;

    // True only when the property is set DIRECTLY here, without consulting the
    // inherit chain. `@property inherits: false` needs the distinction: a
    // custom property an ancestor sets must still take its initial value here,
    // which contains() alone cannot express.
    bool contains_own(std::string_view property) const;

    // !important tracking, consulted when a later declaration tries to
    // overwrite an earlier important one.
    bool is_important(int property_id) const;
    void set_important(int property_id, bool important);

    // Removes a directly-set slot so reads fall through to inherit/initial
    // again. Used when a declaration turns out to be invalid at
    // computed-value time.
    void unset(int property_id);
    void clear();

    // The parsed form of a slot, parsed once and kept. Null when the value does
    // not parse — a result that is itself cached, so a malformed declaration
    // costs one parse rather than one per read.
    //
    // Resolves through the inherit chain like get() does, and shares the
    // ancestor's cache entry when it does, so an inherited property is parsed
    // once for a subtree rather than once per element.
    //
    // Lifetime: valid until this style's slot is written or cleared. Layout
    // reads happen after the cascade has finished, so in practice the value
    // lives as long as the style.
    const CssValue* parsed(int property_id) const;
    const CssValue* parsed(std::string_view property) const;
    int set_count() const { return set_count_; }
    int64_t version() const { return version_; }

    const std::vector<uint64_t>& occupied_bits() const { return occupied_bits_; }
    const std::map<std::string, std::string>& custom_properties() const { return custom_; }

    // Ids set DIRECTLY on this style (not inherited, not initial), ascending.
    std::vector<int> set_ids() const;

private:
    void ensure_capacity(int id);

    std::vector<std::string> values_;
    // Parallel to values_. `parsed_ready_` distinguishes "not parsed yet" from
    // "parsed, and the result was null" — without it a malformed value would be
    // re-parsed on every read, which is the case the cache most needs to cover.
    mutable std::vector<CssValuePtr> parsed_;
    mutable std::vector<bool> parsed_ready_;
    std::vector<bool> occupied_;
    std::vector<uint64_t> occupied_bits_;
    std::vector<bool> important_;
    std::map<std::string, std::string> custom_;
    const ComputedStyle* parent_ = nullptr;
    int set_count_ = 0;
    int64_t version_ = 0;
};

} // namespace weva
