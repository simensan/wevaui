#pragma once
#include "weva/css_properties.h"
#include "weva/css_value.h"

#include <cstdint>
#include <array>
#include <map>
#include <memory>
#include <string>
#include <string_view>
#include <vector>

// Ports Runtime/Css/Cascade/ComputedStyle.cs.
//
// Property ids index a compact slot table; raw strings live in stable pages
// allocated only for properties written to this style. A typical style sets
// about a dozen of the 334 registered properties. Allocating all 334 strings
// per element dominated cold declaration application. Lookups remain indexed,
// and growing storage cannot invalidate views of another property's string.
// Custom properties (`--foo`) have no id and spill to a side map.
//
// The C# keeps BOTH a bool[] and a parallel ulong[] bitset — the bool[] for
// single-load hot readers, the bitset so FillInherited can iterate
// `(parent & ~child & inheritedMask)` in O(words) instead of scanning every
// registered id. Both are kept here for the same reason.
//
// The per-slot parsed-CssValue cache IS ported, after the measurement this
// comment used to wait for. `Tools/weva_bench` put 97% of a layout pass's
// heap allocations — 10.9 MB of 11.1 on a 1691-box document — in
// parse_css_value, because resolve_length re-parsed the raw declaration text
// on every read, several times per box wherever shrink-to-fit lays a box out
// repeatedly. The cache is a memo and changes no semantics: the same string
// parses to the same value.

namespace weva {

class ComputedStyle {
public:
    // Transparent comparison lets reads use string_view without allocating
    // an owned key. Stored names and values still belong to the style.
    using CustomPropertyMap = std::map<std::string, std::string, std::less<>>;
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
    // The resolved font-size, remembered with its parent size and the numeric
    // layout context used to resolve relative/physical units. font_size_px is
    // called several times per box and again for the parent, and a calc()
    // font-size was re-evaluated on every one of them.
    // Mutable and public because it is pure memoisation of a pure function --
    // it changes no answer, only how often the answer is derived. Invalidated
    // by the cascade writing a new value, like the parsed-value memo beside it.
    mutable double font_size_memo_px = 0;
    mutable double font_size_memo_parent = -1;
    // Viewport width/height, root font size, root line height and DPI, in that
    // order. Parent size alone cannot detect changes to vw/rem/rlh/pt inputs.
    mutable std::array<double, 5> font_size_memo_context{};
    // Tied to the style's VERSION rather than invalidated by hand at each
    // mutation. Every write already bumps the version, so the memo cannot
    // outlive the value it describes -- which the by-hand version got wrong on
    // its first attempt by missing the main set().
    mutable int64_t font_size_memo_version = -1;
    // Only an explicitly owned px/number value can ignore both context and
    // parent size. Inherited values keep the ordinary dependency checks.
    mutable bool font_size_memo_absolute = false;
    // insets_replace_layout's answer, tied to version() the same way. It reads
    // only this style's own non-inherited insets and sizes, and is asked for
    // every out-of-flow box on every layout pass.
    mutable int64_t insets_memo_version = -1;
    mutable bool insets_memo = false;


    void set_inherit_parent(const ComputedStyle* parent) { parent_ = parent; }
    const ComputedStyle* inherit_parent() const { return parent_; }

    // A materialized inherit/unset (or pseudo inheritance) retains the raw
    // parent string for style queries, but font resolution must inherit the
    // parent's computed size instead of applying that string again.
    bool font_size_inherited() const { return font_size_inherited_; }
    void mark_font_size_inherited();
    // Relative line-height lengths inherit computed pixels, while numbers
    // and normal remain relative to the descendant's font. Keep the source
    // of materialized inherit/unset and pseudo values until resolution.
    bool line_height_inherited() const { return line_height_inherited_; }
    void mark_line_height_inherited();

    // Raw string access by id. Reads through the inherit chain and then the
    // registry's initial value. Relative font sizes still need font_size_px
    // to obtain computed pixels. contains() tests THIS style's own slot.
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
    static void report_storage_profile();

    const std::vector<uint64_t>& occupied_bits() const { return occupied_bits_; }
    const CustomPropertyMap& custom_properties() const { return custom_; }

    // Ids set DIRECTLY on this style (not inherited, not initial), ascending.
    std::vector<int> set_ids() const;
    // Snapshot in ascending order, retaining caller-owned capacity.
    void copy_set_ids(std::vector<int>& out) const;

    // Compares against a freshly computed style for the same element.
    //
    // An update recomputes every element's style whether or not anything about
    // it changed, and then rebuilds the box tree and lays the document out
    // again on the assumption that it did. This is how a pass finds out: the
    // ids whose value differs are appended to `changed_ids`, and *unattributed
    // is set when something differs that no id names -- a custom property, or
    // the inheritance parent -- which a caller must treat as the broadest kind
    // of change it knows, since it cannot tell what the difference reaches.
    //
    // Returns true when anything at all differs.
    bool differs_from(const ComputedStyle& other, std::vector<int>* changed_ids,
                      bool* unattributed) const;

private:
    void ensure_capacity(int id);

    static constexpr size_t kValuesPerPage = 16;
    using ValuePage = std::array<std::string, kValuesPerPage>;
    std::vector<std::unique_ptr<ValuePage>> values_;
    // Zero means no slot has been assigned; otherwise the value is slot + 1.
    // Presence remains separate, so unset can preserve reusable string storage.
    std::vector<uint32_t> value_slots_;
    size_t value_count_ = 0;
    const std::string& own_value(size_t id) const;
    std::string& ensure_value(size_t id);
    // Indexed by property id. `parsed_ready_` distinguishes "not parsed yet" from
    // "parsed, and the result was null" — without it a malformed value would be
    // re-parsed on every read, which is the case the cache most needs to cover.
    mutable std::vector<CssValuePtr> parsed_;
    mutable std::vector<bool> parsed_ready_;
    // uint8_t, not bool. std::vector<bool> is a bitset, so every presence
    // check -- and `get` does one for every property read in a layout pass --
    // paid a shift and a mask to extract one bit. The C# this ports keeps a
    // bool[] beside the ulong[] bitset for exactly this reason, "for
    // single-load hot readers", and vector<bool> is not that. The bitset is
    // still here as occupied_bits_ for the word-at-a-time walks that want it.
    //
    // It costs a byte per registered property per style rather than a bit:
    // 334 against 42, or about a megabyte on the largest page in the corpus.
    std::vector<uint8_t> occupied_;
    std::vector<uint64_t> occupied_bits_;
    std::vector<bool> important_;
    CustomPropertyMap custom_;
    const ComputedStyle* parent_ = nullptr;
    bool font_size_inherited_ = false;
    bool line_height_inherited_ = false;
    int set_count_ = 0;
    int64_t version_ = 0;
};

} // namespace weva
