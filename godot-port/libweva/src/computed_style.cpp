#include "weva/computed_style.h"

#include <algorithm>

namespace weva {

namespace {
int64_t g_version_counter = 0;

int64_t next_version() { return ++g_version_counter; }

// The counter as it stands, without advancing it. The inherit memo keys on
// this rather than on the style's own version: what it caches is an ANCESTOR,
// and an ancestor changing does not touch the descendant's version. Any write
// to any style advances the counter, so every memo lapses together -- which is
// blunt, and correct. Keying it on the style's own version instead served a
// stale ancestor the moment one was written to, and the cascade store tests
// caught it.
int64_t current_version() { return g_version_counter; }
const std::string kEmpty;
}  // namespace

void ComputedStyle::ensure_capacity(int id) {
    auto need = static_cast<std::size_t>(id) + 1;
    if (values_.size() >= need) return;
    // To the FULL registry, not to the one property that asked. Growing by one
    // meant a style filled in ascending id order resized six vectors 334
    // times: three of them std::vector<bool>, whose resize is a bit-level
    // fill. Cascading 10,000 rows spent 3 million calls in that one function
    // -- the single largest cost in a first layout, ahead of layout itself.
    //
    // The registry's size is fixed at startup, so this is the size every style
    // reaches anyway; taking it at once costs one allocation each instead of
    // hundreds, and the memory was going to be used.
    const auto full = static_cast<std::size_t>(CssPropertyRegistry::instance().count());
    if (full > need) need = full;
    values_.resize(need);
    parsed_.resize(need);
    parsed_ready_.resize(need, false);
    occupied_.resize(need, 0);
    important_.resize(need, false);
    occupied_bits_.resize((need + 63) / 64, 0);
}

bool ComputedStyle::contains(int id) const {
    if (id < 0 || static_cast<std::size_t>(id) >= occupied_.size()) return false;
    return occupied_[static_cast<std::size_t>(id)] != 0;
}

std::string_view ComputedStyle::get(int id) const {
    if (contains(id)) return values_[static_cast<std::size_t>(id)];
    // Not set here: walk the inherit chain for inherited properties, then fall
    // back to the registry's shared initial value. Neither path copies.
    //
    // ITERATIVELY. Recursing into parent_->get() asked the registry whether the
    // property inherits once per ancestor, and reached the singleton through
    // its guard each time -- so reading an inherited property on a leaf ten
    // deep did ten of both. Whether a property inherits is a fact about the
    // property, so it is settled once and the chain is then just a walk.
    const auto& reg = CssPropertyRegistry::instance();
    if (parent_ && reg.is_inherited(id)) {
        // Walked, not memoised. Caching the ancestor that sets each inherited
        // property looked obviously worth it -- the walk is O(depth) and runs
        // for every read of every inherited property on every box -- and it
        // measured 4 to 6 per cent SLOWER on the larger pages under an
        // interleaved A/B. Real chains are one or two links, so the memo's
        // guard costs more than the walk it skips.
        for (const ComputedStyle* p = parent_; p; p = p->parent_) {
            if (p->contains(id)) return p->values_[static_cast<std::size_t>(id)];
        }
    }
    return reg.initial_value(id);
}

bool ComputedStyle::try_get(int id, std::string* out) const {
    if (!contains(id)) return false;
    *out = values_[static_cast<std::size_t>(id)];
    return true;
}

void ComputedStyle::set(int id, std::string_view value) {
    if (id < 0) return;
    ensure_capacity(id);
    auto i = static_cast<std::size_t>(id);
    // A no-op write must not bump the version — the whole invalidation
    // architecture keys caches on version numbers, so a spurious bump costs a
    // re-cascade of everything downstream.
    if (occupied_[i] != 0 && values_[i] == value) return;
    if (occupied_[i] == 0) {
        occupied_[i] = 1;
        occupied_bits_[i >> 6] |= 1ULL << (i & 63);
        ++set_count_;
    }
    // assign(), not a constructed temporary: `= std::string(value)` builds a
    // string, allocates for anything past the small-string buffer, moves it
    // in and frees the slot's existing allocation. assign() reuses the
    // capacity the slot already has, which after the first pass is almost
    // always enough. A cascade performs ~178 of these per element.
    values_[i].assign(value.data(), value.size());
    // The memo describes the old string.
    parsed_[i].reset();
    parsed_ready_[i] = false;
    version_ = next_version();
}

bool ComputedStyle::is_important(int id) const {
    if (id < 0 || static_cast<std::size_t>(id) >= important_.size()) return false;
    return important_[static_cast<std::size_t>(id)];
}

void ComputedStyle::set_important(int id, bool important) {
    if (id < 0) return;
    ensure_capacity(id);
    important_[static_cast<std::size_t>(id)] = important;
}

std::string_view ComputedStyle::get(std::string_view property) const {
    int id = CssPropertyRegistry::instance().id_of(property);
    if (id != kCustomPropertyId) return get(id);
    auto it = custom_.find(std::string(property));
    if (it != custom_.end()) return it->second;
    // Custom properties inherit by default (CSS Custom Properties L1 §2).
    // `@property inherits: false` is honoured by the cascade stamping the
    // descriptor's initial value directly onto every element, so this walk
    // never reaches an ancestor for such a property.
    if (parent_) return parent_->get(property);
    return {};
}

void ComputedStyle::set(std::string_view property, std::string_view value) {
    int id = CssPropertyRegistry::instance().id_of(property);
    if (id != kCustomPropertyId) { set(id, value); return; }
    std::string key(property);
    auto it = custom_.find(key);
    if (it != custom_.end() && it->second == value) return;   // no-op, no bump
    custom_[key] = std::string(value);
    version_ = next_version();
}

bool ComputedStyle::contains_own(std::string_view property) const {
    int id = CssPropertyRegistry::instance().id_of(property);
    if (id != kCustomPropertyId) return contains(id);
    return custom_.find(std::string(property)) != custom_.end();
}

bool ComputedStyle::contains(std::string_view property) const {
    int id = CssPropertyRegistry::instance().id_of(property);
    if (id != kCustomPropertyId) return contains(id);
    if (custom_.find(std::string(property)) != custom_.end()) return true;
    return parent_ && parent_->contains(property);
}

const CssValue* ComputedStyle::parsed(int id) const {
    if (id < 0) return nullptr;
    const auto& reg = CssPropertyRegistry::instance();
    if (!contains(id)) {
        // Share the ancestor's entry rather than parsing the same inherited
        // string again at every level of the subtree.
        if (parent_ && reg.is_inherited(id)) return parent_->parsed(id);
        // An initial value is shared and immutable, but the memo lives on the
        // style, so it is cached here like any other slot. The registry has no
        // storage of its own to hang it from.
    }
    const auto i = static_cast<std::size_t>(id);
    if (i >= parsed_ready_.size()) {
        // ensure_capacity is non-const; the memo is the only mutable state, so
        // it is grown directly.
        const std::size_t need = i + 1;
        parsed_.resize(need);
        parsed_ready_.resize(need, false);
    }
    if (!parsed_ready_[i]) {
        CssParseError err;
        const std::string_view raw = get(id);
        parsed_[i] = raw.empty() ? nullptr : parse_css_value(raw, &err);
        parsed_ready_[i] = true;
    }
    return parsed_[i].get();
}

const CssValue* ComputedStyle::parsed(std::string_view property) const {
    const int id = CssPropertyRegistry::instance().id_of(property);
    // A custom property has no slot to memoise against; it is parsed on demand
    // by the variable resolver rather than here.
    if (id == kCustomPropertyId) return nullptr;
    return parsed(id);
}

void ComputedStyle::unset(int id) {
    if (!contains(id)) return;
    auto i = static_cast<std::size_t>(id);
    occupied_[i] = 0;
    occupied_bits_[i >> 6] &= ~(1ULL << (i & 63));
    values_[i].clear();
    parsed_[i].reset();
    parsed_ready_[i] = false;
    important_[i] = false;
    --set_count_;
    version_ = next_version();
}

void ComputedStyle::clear() {
    parent_ = nullptr;
    values_.clear();
    parsed_.clear();
    parsed_ready_.clear();
    occupied_.clear();
    occupied_bits_.clear();
    important_.clear();
    custom_.clear();
    set_count_ = 0;
    version_ = next_version();
}

bool ComputedStyle::differs_from(const ComputedStyle& other, std::vector<int>* changed_ids,
                                 bool* unattributed) const {
    bool any = false;
    // Neither of these names a property, so a caller cannot narrow what the
    // difference affects. A custom property reaches whatever var() read it,
    // and those reads were substituted during the cascade -- so a change that
    // mattered also shows up as a changed value below, and one that did not
    // still has to be reported, because nothing here can prove it did not.
    if (parent_ != other.parent_ || custom_ != other.custom_) {
        any = true;
        if (unattributed) *unattributed = true;
    }
    const std::size_t n = std::max(values_.size(), other.values_.size());
    for (std::size_t i = 0; i < n; ++i) {
        const bool a = i < occupied_.size() && occupied_[i] != 0;
        const bool b = i < other.occupied_.size() && other.occupied_[i] != 0;
        if (!a && !b) continue;
        bool same;
        if (a == b) {
            same = values_[i] == other.values_[i] &&
                   (i < important_.size() && important_[i]) ==
                       (i < other.important_.size() && other.important_[i]);
        } else {
            // Set on one side and not the other is not yet a difference: a
            // property set to what it would have been anyway computes the
            // same. Asking for the VALUE settles it, and the question here is
            // whether the computed value changed -- nothing downstream can
            // tell how it was arrived at.
            //
            // Comparing set-ness alone reported `padding: 0` against an unset
            // padding as a change, and one such report costs a relayout of the
            // whole document.
            same = get(static_cast<int>(i)) == other.get(static_cast<int>(i));
        }
        if (same) continue;
        any = true;
        if (changed_ids) changed_ids->push_back(static_cast<int>(i));
    }
    return any;
}

std::vector<int> ComputedStyle::set_ids() const {
    std::vector<int> out;
    for (std::size_t i = 0; i < occupied_.size(); ++i) {
        if (occupied_[i] != 0) out.push_back(static_cast<int>(i));
    }
    return out;
}

} // namespace weva
